﻿using Lime.Protocol.Http.Processors;
using Lime.Protocol.Http.Serialization;
using Lime.Protocol.Http.Storage;
using Lime.Protocol.Network;
using Lime.Protocol.Serialization;
using Lime.Protocol.Server;
using Lime.Protocol.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Lime.Protocol.Http
{
    /*
# Receive from the channel (long polling)
GET /messages/

# Stored messages
GET /storage/messages/

# Send to the channel, fire-and-forget
POST /messages/

# Send to the channel, with notification
POST /messages/?id=a9173c7d-038c-4101-b547-939c25d8053e

# Commands only to the channel (not stored)
GET /commands/presence/
POST /commands/presence/
DELETE /commands/presence/

# Receive from the storage
GET /storage/notifications/

# Send to the channel
POST /notifications/?id=a9173c7d-038c-4101-b547-939c25d8053e
    */


    /// <summary>
    /// Implements a HTTP listener server 
    /// that supports an emulation layer 
    /// for the LIME protocol.
    /// </summary>
    public class HttpTransportListener : ITransportListener
    {
        #region Private Fields

        private readonly IDocumentSerializer _serializer;
        private readonly bool _useHttps;
        private readonly bool _writeExceptionsToOutput;
        private readonly TimeSpan _requestTimeout;
        private readonly BufferBlock<ServerHttpTransport> _transportBufferBlock;
        private readonly ConcurrentDictionary<string, ServerHttpTransport> _transportDictionary;               
        private readonly BufferBlock<HttpListenerContext> _listenerInputBufferBlock;
        private readonly ActionBlock<HttpListenerContext> _processContextActionBlock;
        private readonly ConcurrentDictionary<Guid, HttpListenerResponse> _pendingResponsesDictionary;
        private readonly ActionBlock<Envelope> _processTransportOutputActionBlock;


        private readonly IEnvelopeStorage<Message> _messageStorage;
        private readonly IEnvelopeStorage<Notification> _notificationStorage;

        private HttpListener _httpListener;
        private Task _listenerTask;
        private CancellationTokenSource _listenerCancellationTokenSource;
        private readonly string _basePath;
        private readonly string[] _prefixes;


        #endregion


        private UriTemplateTable _uriTemplateTable;

        #region Constructor

        public HttpTransportListener(int port, string hostName = "*", bool useHttps = false, bool writeExceptionsToOutput = true, IEnvelopeStorage<Message> messageStorage = null, IEnvelopeStorage<Notification> notificationStorage = null)
        {
            if (!HttpListener.IsSupported)
            {
                throw new NotSupportedException("Windows XP SP2 or Server 2003 is required to use the HttpListener class.");
            }            

            var scheme = Uri.UriSchemeHttps;
            _useHttps = useHttps;
            if (!useHttps)
            {
                scheme = Uri.UriSchemeHttp;
            }

            _writeExceptionsToOutput = writeExceptionsToOutput;

            _basePath = string.Format("{0}://{1}:{2}", scheme, hostName, port);
            _prefixes = new string[]
            {
                Constants.ROOT + Constants.MESSAGES_PATH + Constants.ROOT,
                Constants.ROOT + Constants.COMMANDS_PATH + Constants.ROOT,
                Constants.ROOT + Constants.NOTIFICATIONS_PATH + Constants.ROOT
            };

            var safeHostName = hostName;
            if (hostName.Equals("*") || hostName.Equals("+"))
            {
                safeHostName = "localhost";
            }

            var baseUri = new Uri(string.Format("{0}://{1}:{2}", scheme, safeHostName, port));
            ListenerUris = _prefixes
                .Select(p => new Uri(baseUri, p))
                .ToArray();

            _messageStorage = messageStorage ?? new DictionaryEnvelopeStorage<Message>();
            _notificationStorage = notificationStorage ?? new DictionaryEnvelopeStorage<Notification>();
            
            _requestTimeout = TimeSpan.FromSeconds(60);
            _serializer = new DocumentSerializer();

            _transportBufferBlock = new BufferBlock<ServerHttpTransport>();            
            _transportDictionary = new ConcurrentDictionary<string, ServerHttpTransport>();
            
            // Pipelines
            _pendingResponsesDictionary = new ConcurrentDictionary<Guid, HttpListenerResponse>();
            _listenerInputBufferBlock = new BufferBlock<HttpListenerContext>();
            _processContextActionBlock = new ActionBlock<HttpListenerContext>(c => ProcessListenerContextAsync(c));
            _listenerInputBufferBlock.LinkTo(_processContextActionBlock);            
            _processTransportOutputActionBlock = new ActionBlock<Envelope>(e => ProcessTransportOutputAsync(e));            


            // New
            var sendCommandProcessor = new SendCommandProcessor(Constants.COMMANDS_PATH, _pendingResponsesDictionary);
            var sendMessageProcessor = new SendMessageProcessor(Constants.MESSAGES_PATH, _pendingResponsesDictionary);

            _uriTemplateTable = new UriTemplateTable(baseUri);
            _uriTemplateTable.KeyValuePairs.Add(new KeyValuePair<UriTemplate, object>(sendCommandProcessor.Template, sendCommandProcessor));
            _uriTemplateTable.KeyValuePairs.Add(new KeyValuePair<UriTemplate, object>(sendMessageProcessor.Template, sendMessageProcessor));
            _uriTemplateTable.MakeReadOnly(true);                     

        }

        #endregion

        #region ITransportListener Members

        public Uri[] ListenerUris { get; private set; }

        public Task StartAsync()
        {
            if (_listenerTask != null)
            {
                throw new InvalidOperationException("The listener is already active");
            }

            _httpListener = new HttpListener();
            _httpListener.AuthenticationSchemes = AuthenticationSchemes.Basic;

            foreach (var prefix in _prefixes)
            {
                _httpListener.Prefixes.Add(_basePath + prefix);
            }

            _httpListener.Start();
            _listenerCancellationTokenSource = new CancellationTokenSource();
            _listenerTask = ListenAsync();

            return Task.FromResult<object>(null);
        }

        public async Task<ITransport> AcceptTransportAsync(CancellationToken cancellationToken)
        {
            if (_listenerTask == null)
            {
                throw new InvalidOperationException("The listener was not started.");
            }

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _listenerCancellationTokenSource.Token);

            var transport = await _transportBufferBlock.ReceiveAsync(linkedCancellationToken.Token).ConfigureAwait(false);
            var link = transport.OutputBuffer.LinkTo(_processTransportOutputActionBlock);
            transport.Closed += (sender, e) => link.Dispose();                        
            return transport;
        }

        public async Task StopAsync()
        {
            if (_listenerTask == null)
            {
                throw new InvalidOperationException("The listener was not started.");
            }

            _listenerCancellationTokenSource.Cancel();
            await _listenerTask.ConfigureAwait(false);
            _listenerTask = null;

            _httpListener.Stop();            
            _httpListener = null;
        }

        #endregion

        #region Private Fields

        /// <summary>
        /// Consumes the http listener.
        /// </summary>
        /// <returns></returns>
        private async Task ListenAsync()
        {
            try
            {
                while (!_listenerCancellationTokenSource.IsCancellationRequested)
                {
                    var context = await _httpListener
                        .GetContextAsync()
                        .WithCancellation(_listenerCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                    if (!await _listenerInputBufferBlock.SendAsync(context, _listenerCancellationTokenSource.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Process a request received by 
        /// the HTTP listener.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task ProcessListenerContextAsync(HttpListenerContext context)
        {
            var match = _uriTemplateTable
                .Match(context.Request.Url)
                .Where(m => ((IRequestProcessor)m.Data).Methods.Contains(context.Request.HttpMethod))
                .FirstOrDefault();

            if (match != null)
            {
                var cancellationToken = _requestTimeout.ToCancellationToken();
                var transport = GetTransport(context);

                Exception exception = null;

                try
                {
                    var session = await transport.GetSessionAsync(cancellationToken).ConfigureAwait(false);
                    context.Response.Headers.Add(Constants.SESSION_ID_HEADER, session.Id.ToString());

                    if (session.State == SessionState.Established)
                    {
                        var processor = (IRequestProcessor)match.Data;
                        await processor.ProcessAsync(context, transport, cancellationToken).ConfigureAwait(false);

                    }
                    else if (session.Reason != null)
                    {
                        context.Response.StatusCode = (int)GetHttpStatusCode(session.Reason.Code);
                        context.Response.StatusDescription = session.Reason.Description;
                        context.Response.Close();
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.Close();
                    }

                }
                catch (LimeException ex)
                {
                    context.Response.StatusCode = (int)GetHttpStatusCode(ex.Reason.Code);
                    context.Response.StatusDescription = ex.Reason.Description;
                    context.Response.Close();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                if (exception != null)
                {
                    if (exception is OperationCanceledException)
                    {
                        await transport.CloseAsync(_listenerCancellationTokenSource.Token).ConfigureAwait(false);
                        context.Response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                        context.Response.Close();
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        if (_writeExceptionsToOutput)
                        {
                            context.Response.ContentType = Constants.TEXT_PLAIN_HEADER_VALUE;
                            using (var writer = new StreamWriter(context.Response.OutputStream))
                            {
                                await writer.WriteAsync(exception.ToString()).ConfigureAwait(false);
                            }
                        }

                        context.Response.Close();
                    }
                }
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Gets the transport instance
        /// for the specified context
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private ServerHttpTransport GetTransport(HttpListenerContext context)
        {
            var identity = (HttpListenerBasicIdentity)context.User.Identity;
            var transportKey = GetTransportKey(identity);

            var transport = _transportDictionary.GetOrAdd(
                transportKey,
                k =>
                {
                    var newTransport = CreateTransport(identity);
                    newTransport.Closing += (sender, e) =>
                    {
                        _transportDictionary.TryRemove(k, out newTransport);
                    };
                    return newTransport;
                });
            return transport;
        }

        /// <summary>
        /// Creates a new instance
        /// of tranport for the
        /// specified identity
        /// </summary>
        /// <param name="identity"></param>
        /// <returns></returns>
        private ServerHttpTransport CreateTransport(HttpListenerBasicIdentity identity)
        {
            var transport = new ServerHttpTransport(identity, _useHttps);
            _transportBufferBlock.Post(transport);
            return transport;
        }

        /// <summary>
        /// Gets a hashed key based on
        /// the identity and password.
        /// </summary>
        /// <param name="identity"></param>
        /// <returns></returns>
        private static string GetTransportKey(HttpListenerBasicIdentity identity)
        {
            return string.Format("{0}:{1}", identity.Name, identity.Password).ToSHA1HashString();
        }

        /// <summary>
        /// Consumes the transports outputs.
        /// </summary>
        /// <param name="envelope"></param>
        /// <returns></returns>
        private async Task ProcessTransportOutputAsync(Envelope envelope)
        {
            Exception exception = null;

            try
            {
                HttpListenerResponse response;

                if (envelope is Message)
                {
                    await _messageStorage.StoreEnvelopeAsync(envelope.To.ToIdentity(), (Message)envelope).ConfigureAwait(false);
                }
                else if (_pendingResponsesDictionary.TryRemove(envelope.Id, out response))
                {
                    if (envelope is Notification)
                    {
                        ProcessNotificationResult((Notification)envelope, response);
                    }
                    else if (envelope is Command)
                    {
                        await ProcessCommandResultAsync((Command)envelope, response).ConfigureAwait(false);
                    }
                    else
                    {
                        // Message and sessions should not be here...
                        // Put the response back
                        _pendingResponsesDictionary.TryAdd(envelope.Id, response);

                        // Stores the envelope
                        
                    }

                    response.Close();
                }                
                else if (envelope is Notification)
                {
                    await _notificationStorage.StoreEnvelopeAsync(envelope.To.ToIdentity(), (Notification)envelope).ConfigureAwait(false);
                }
                else
                {
                    // Register the error, but do not throw an exception
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (exception != null)
            {

            }
        }
   

        private HttpStatusCode GetHttpStatusCode(int reasonCode)
        {            
            if (reasonCode >= 20 && reasonCode < 30)
            {
                // Validation errors
                return HttpStatusCode.BadRequest;
            }
            else if ((reasonCode >= 10 && reasonCode < 20) || (reasonCode >= 30 && reasonCode < 40))
            {
                // Session or Authorization errors
                return HttpStatusCode.Unauthorized;
            }            
            
            return HttpStatusCode.Forbidden;
        }

        private void ProcessNotificationResult(Notification notification, HttpListenerResponse response)
        {
            if (notification.Event == Event.Dispatched)
            {
                response.StatusCode = (int)HttpStatusCode.Created;
            }
            else if (notification.Event == Event.Failed)
            {
                response.StatusCode = (int)GetHttpStatusCode(notification.Reason.Code);
                response.StatusDescription = notification.Reason.Description;
            }
        }

        private async Task ProcessCommandResultAsync(Command command, HttpListenerResponse response)
        {
            if (command.Status == CommandStatus.Success)
            {
                response.StatusCode = (int)HttpStatusCode.Created;
            }
            else
            {
                response.StatusCode = (int)GetHttpStatusCode(command.Reason.Code);
                response.StatusDescription = command.Reason.Description;
            }

            if (command.Resource != null)
            {
                var mediaType = command.Resource.GetMediaType();
                response.Headers.Add(Constants.CONTENT_TYPE_HEADER, mediaType.ToString());

                using (var writer = new StreamWriter(response.OutputStream))
                {
                    var documentString = _serializer.Serialize(command.Resource);
                    await writer.WriteAsync(documentString).ConfigureAwait(false);
                }
            }
        }       

        #endregion
    }
}