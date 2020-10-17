using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Hazel
{
    public class MessageWriter : IDisposable
    {
        private const int MaxPacketSize = 65535;
        private static readonly MemoryPool<byte> Pool = MemoryPool<byte>.Shared;

        private readonly IMemoryOwner<byte> _memoryOwner;
        private readonly Stack<int> _messageStarts;

        private MessageWriter(IMemoryOwner<byte> memoryOwner, SendOption option)
        {
            _memoryOwner = memoryOwner;
            _messageStarts = new Stack<int>();

            SendOption = option;

            Clear(option);
        }

        public SendOption SendOption { get; private set; }
        public int Length { get; private set; }
        public int Position { get; private set; }
        public ReadOnlyMemory<byte> Data => _memoryOwner.Memory;
        internal Memory<byte> DataWritable => _memoryOwner.Memory;

        /// <summary>
        ///     Creates a new <see cref="MessageWriter"/>.
        /// </summary>
        /// <param name="sendOption">The option specifying how the message should be sent.</param>
        /// <returns></returns>
        public static MessageWriter Get(SendOption sendOption = SendOption.None)
        {
            if (sendOption == SendOption.Reliable)
            {
                Console.WriteLine("Creating reliable");
            }
            return new MessageWriter(Pool.Rent(MaxPacketSize), sendOption);
        }

        public void StartMessage(byte typeFlag)
        {
            _messageStarts.Push(Position);
            Position += 2; // Skip for size
            Write(typeFlag);
        }

        public void EndMessage()
        {
            var lastMessageStart = _messageStarts.Pop();
            var length = (ushort)(Position - lastMessageStart - 3); // Minus message header
            BinaryPrimitives.WriteUInt16LittleEndian(DataWritable.Span.Slice(lastMessageStart), length);
        }

        public void CancelMessage()
        {
            Position = _messageStarts.Pop();
            Length = Position;
        }

        public void Clear(SendOption sendOption)
        {
            _messageStarts.Clear();

            SendOption = sendOption;
            DataWritable.Span[0] = (byte)sendOption;

            switch (sendOption)
            {
                case SendOption.None:
                    Length = Position = 1;
                    break;
                case SendOption.Reliable:
                    Length = Position = 3;
                    break;
            }
        }

        #region WriteMethods

        public void Write(bool value)
        {
            DataWritable.Span[Position++] = value ? 1 : 0;

            if (Position > Length)
            {
                Length = Position;
            }
        }

        public void Write(sbyte value)
        {
            DataWritable.Span[Position++] = (byte)value;
            if (Position > Length) Length = Position;
        }

        public void Write(byte value)
        {
            DataWritable.Span[Position++] = value;
            if (Position > Length) Length = Position;
        }

        public void Write(short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(DataWritable.Span.Slice(Position), value);
            Position += 2;
            if (Position > Length) Length = Position;
        }

        public void Write(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(DataWritable.Span.Slice(Position), value);
            Position += 2;
            if (Position > Length) Length = Position;
        }

        public void Write(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(DataWritable.Span.Slice(Position), value);
            Position += 4;
            if (Position > Length) Length = Position;
        }

        public void Write(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(DataWritable.Span.Slice(Position), value);
            Position += 4;
            if (Position > Length) Length = Position;
        }

        public void Write(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(DataWritable.Span.Slice(Position), value);
            Position += 4;
            if (Position > Length) Length = Position;
        }

        public void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WritePacked(bytes.Length);
            Write(bytes);
        }

        public void WriteBytesAndSize(byte[] bytes)
        {
            WritePacked((uint)bytes.Length);
            Write(bytes);
        }

        public void WriteBytesAndSize(byte[] bytes, int length)
        {
            WritePacked((uint)length);
            Write(bytes, length);
        }

        public void WriteBytesAndSize(byte[] bytes, int offset, int length)
        {
            WritePacked((uint)length);
            Write(bytes, offset, length);
        }

        public void Write(ReadOnlyMemory<byte> data)
        {
            data.CopyTo(DataWritable.Slice(Position));
            Position += data.Length;
        }

        public void Write(byte[] bytes, int offset, int length)
        {
            Write(bytes.AsMemory(offset, length));
        }

        public void Write(byte[] bytes, int length)
        {
            Write(bytes.AsMemory(0, length));
        }

        public void WritePacked(int value)
        {
            WritePacked((uint)value);
        }

        public void WritePacked(uint value)
        {
            do
            {
                byte b = (byte)(value & 0xFF);
                if (value >= 0x80)
                {
                    b |= 0x80;
                }

                Write(b);
                value >>= 7;
            } while (value > 0);
        }

        #endregion

        public bool HasBytes(int expected)
        {
            if (SendOption == SendOption.None)
            {
                return Length > 1 + expected;
            }

            return Length > 3 + expected;
        }

        public byte[] ToByteArray(bool includeHeader)
        {
            if (includeHeader)
            {
                return Data.ToArray();
            }

            return SendOption switch
            {
                SendOption.Reliable => Data.Slice(3).ToArray(),
                SendOption.None => Data.Slice(1).ToArray(),
                _ => throw new NotImplementedException()
            };
        }

        public void Dispose()
        {
            Console.WriteLine($"Disposing {SendOption}");
            _memoryOwner.Dispose();
        }
    }
}
