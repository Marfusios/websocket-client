using System;
using System.Buffers;

namespace Websocket.Client
{
    internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly ArrayPool<byte> _pool;
        private byte[] _buffer;
        private int _written;

        public PooledBufferWriter(int initialCapacity)
            : this(initialCapacity, ArrayPool<byte>.Shared)
        {
        }

        private PooledBufferWriter(int initialCapacity, ArrayPool<byte> pool)
        {
            if (initialCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));

            _pool = pool;
            _buffer = _pool.Rent(initialCapacity);
        }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public ArraySegment<byte> WrittenSegment => new ArraySegment<byte>(_buffer, 0, _written);

        public int Capacity => _buffer.Length;

        public void Advance(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (_written > _buffer.Length - count)
                throw new InvalidOperationException("Cannot advance past the end of the buffer.");

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public ArraySegment<byte> GetSegment(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return new ArraySegment<byte>(_buffer, _written, _buffer.Length - _written);
        }

        public void Clear()
        {
            _written = 0;
        }

        public void Clear(int maxRetainedCapacity)
        {
            if (maxRetainedCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxRetainedCapacity));

            _written = 0;

            if (_buffer.Length <= maxRetainedCapacity)
                return;

            var buffer = _buffer;
            _buffer = _pool.Rent(maxRetainedCapacity);
            _pool.Return(buffer);
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = Array.Empty<byte>();
            _written = 0;

            if (buffer.Length > 0)
                _pool.Return(buffer);
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint));

            if (sizeHint == 0)
                sizeHint = 1;

            if (sizeHint <= _buffer.Length - _written)
                return;

            Grow(sizeHint);
        }

        private void Grow(int sizeHint)
        {
            var newSize = checked(_buffer.Length + Math.Max(sizeHint, _buffer.Length));
            var newBuffer = _pool.Rent(newSize);

            _buffer.AsSpan(0, _written).CopyTo(newBuffer);
            _pool.Return(_buffer);
            _buffer = newBuffer;
        }
    }
}
