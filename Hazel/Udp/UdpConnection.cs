﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;
using Serilog;

namespace Hazel.Udp
{
    /// <summary>
    ///     Represents a connection that uses the UDP protocol.
    /// </summary>
    /// <inheritdoc />
    public abstract partial class UdpConnection : NetworkConnection
    {
        protected static readonly byte[] EmptyDisconnectBytes = { (byte)UdpSendOption.Disconnect };

        private static readonly ILogger Logger = Log.ForContext<UdpConnection>();
        private readonly ConnectionListener _listener;

        private bool _isDisposing;
        private bool _isFirst = true;
        private Task _executingTask;

        protected UdpConnection(ConnectionListener listener)
        {
            _listener = listener;
            Pipeline = new Pipe();
        }

        internal Pipe Pipeline { get; }

        public Task StartAsync()
        {
            // Store the task we're executing
            _executingTask = ReadAsync();

            // If the task is completed then return it, this will bubble cancellation and failure to the caller
            if (_executingTask.IsCompleted)
            {
                return _executingTask;
            }

            // Otherwise it's running
            return Task.CompletedTask;
        }

        public void Stop()
        {
            // Stop called without start
            if (_executingTask == null)
            {
                return;
            }

            // Cancel reader.
            Pipeline.Reader.CancelPendingRead();

            // Remove references.
            if (!_isDisposing)
            {
                Dispose(true);
            }
        }

        private async Task ReadAsync()
        {
            while (true)
            {
                var result = await Pipeline.Reader.ReadAsync();
                if (result.IsCanceled)
                {
                    // The read was canceled.
                    break;
                }

                try
                {
                    if (!result.Buffer.IsSingleSegment)
                    {
                        Console.WriteLine("Not result.Buffer.IsSingleSegment");
                    }

                    Console.WriteLine($"{EndPoint}: {result.Buffer.Length} ({result.Buffer.Start.GetInteger()} - {result.Buffer.End.GetInteger()})");

                    await HandleReceive(new MessageReader(result.Buffer.IsSingleSegment
                        ? result.Buffer.First
                        : result.Buffer.ToArray()));
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Exception during ReadAsync");
                    Dispose(true);
                    break;
                }
                finally
                {
                    Pipeline.Reader.AdvanceTo(result.Buffer.End, result.Buffer.End);
                }
            }
        }

        /// <summary>
        ///     Writes the given bytes to the connection.
        /// </summary>
        /// <param name="bytes">The bytes to write.</param>
        protected abstract ValueTask WriteBytesToConnection(byte[] bytes, int length);

        /// <inheritdoc/>
        public override async ValueTask Send(MessageWriter msg)
        {
            if (this._state != ConnectionState.Connected)
                throw new InvalidOperationException("Could not send data as this Connection is not connected. Did you disconnect?");

            byte[] buffer = new byte[msg.Length];
            Buffer.BlockCopy(msg.Buffer, 0, buffer, 0, msg.Length);

            switch (msg.SendOption)
            {
                case SendOption.Reliable:
                    ResetKeepAliveTimer();

                    AttachReliableID(buffer, 1, buffer.Length);
                    await WriteBytesToConnection(buffer, buffer.Length);
                    Statistics.LogReliableSend(buffer.Length - 3, buffer.Length);
                    break;

                default:
                    await WriteBytesToConnection(buffer, buffer.Length);
                    Statistics.LogUnreliableSend(buffer.Length - 1, buffer.Length);
                    break;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        ///     <include file="DocInclude/common.xml" path="docs/item[@name='Connection_SendBytes_General']/*" />
        ///     <para>
        ///         Udp connections can currently send messages using <see cref="SendOption.None"/> and
        ///         <see cref="SendOption.Reliable"/>. Fragmented messages are not currently supported and will default to
        ///         <see cref="SendOption.None"/> until implemented.
        ///     </para>
        /// </remarks>
        public override async ValueTask SendBytes(byte[] bytes, SendOption sendOption = SendOption.None)
        {
            //Add header information and send
            await HandleSend(bytes, (byte)sendOption);
        }
        
        /// <summary>
        ///     Handles the reliable/fragmented sending from this connection.
        /// </summary>
        /// <param name="data">The data being sent.</param>
        /// <param name="sendOption">The <see cref="SendOption"/> specified as its byte value.</param>
        /// <param name="ackCallback">The callback to invoke when this packet is acknowledged.</param>
        /// <returns>The bytes that should actually be sent.</returns>
        protected async ValueTask HandleSend(byte[] data, byte sendOption, Action ackCallback = null)
        {
            switch (sendOption)
            {
                case (byte)UdpSendOption.Ping:
                case (byte)SendOption.Reliable:
                case (byte)UdpSendOption.Hello:
                    await ReliableSend(sendOption, data, ackCallback);
                    break;
                                    
                //Treat all else as unreliable
                default:
                    await UnreliableSend(sendOption, data);
                    break;
            }
        }

        /// <summary>
        ///     Handles the receiving of data.
        /// </summary>
        /// <param name="message">The buffer containing the bytes received.</param>
        protected async ValueTask HandleReceive(MessageReader message)
        {
            // Check if the first message received is the hello packet.
            if (_isFirst)
            {
                _isFirst = false;

                // Slice 4 bytes to get handshake data.
                await _listener.InvokeNewConnection(message.Slice(4), this);
            }

            switch (message.Buffer.Span[0])
            {
                //Handle reliable receives
                case (byte)SendOption.Reliable:
                    await ReliableMessageReceive(message);
                    break;

                //Handle acknowledgments
                case (byte)UdpSendOption.Acknowledgement:
                    AcknowledgementMessageReceive(message.Buffer.Span);
                    break;

                //We need to acknowledge hello and ping messages but dont want to invoke any events!
                case (byte)UdpSendOption.Ping:
                    await ProcessReliableReceive(message.Buffer, 1);
                    Statistics.LogHelloReceive(message.Length);
                    break;
                case (byte)UdpSendOption.Hello:
                    await ProcessReliableReceive(message.Buffer, 1);
                    Statistics.LogHelloReceive(message.Length);
                    break;

                case (byte)UdpSendOption.Disconnect:
                    await DisconnectRemote("The remote sent a disconnect request", message.Slice(1));
                    break;
                    
                //Treat everything else as unreliable
                default:
                    await InvokeDataReceived(message.Slice(1), SendOption.None);
                    Statistics.LogUnreliableReceive(message.Length - 1, message.Length);
                    break;
            }
        }

        /// <summary>
        ///     Sends bytes using the unreliable UDP protocol.
        /// </summary>
        /// <param name="sendOption">The SendOption to attach.</param>
        /// <param name="data">The data.</param>
        ValueTask UnreliableSend(byte sendOption, byte[] data)
        {
            return UnreliableSend(sendOption, data, 0, data.Length);
        }

        /// <summary>
        ///     Sends bytes using the unreliable UDP protocol.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <param name="sendOption">The SendOption to attach.</param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        async ValueTask UnreliableSend(byte sendOption, byte[] data, int offset, int length)
        {
            byte[] bytes = new byte[length + 1];

            //Add message type
            bytes[0] = sendOption;

            //Copy data into new array
            Buffer.BlockCopy(data, offset, bytes, bytes.Length - length, length);

            //Write to connection
            await WriteBytesToConnection(bytes, bytes.Length);

            Statistics.LogUnreliableSend(length, bytes.Length);
        }
                
        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isDisposing = true;

                Stop();
                DisposeKeepAliveTimer();
                DisposeReliablePackets();
            }

            base.Dispose(disposing);
        }
    }
}
