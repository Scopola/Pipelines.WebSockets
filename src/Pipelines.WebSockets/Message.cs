﻿using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.IO.Pipelines.Text.Primitives;

namespace Pipelines.WebSockets
{
    public struct Message : IMessageWriter
    {
        private ReadableBuffer _buffer;
        private List<PreservedBuffer> _buffers;
        private int _mask;
        private string _text;
        public bool IsFinal { get; }
        internal Message(ReadableBuffer buffer, int mask, bool isFinal)
        {
            this._buffer = buffer;
            this._mask = mask;
            _text = null;
            IsFinal = isFinal;
            _buffers = null;
        }

        internal WritableBufferAwaitable WriteAsync(IPipeWriter output)
        {
            var write = output.Alloc();
            if(_buffers != null)
            {
                foreach (var buffer in _buffers)
                {
                    write.Append(buffer.Buffer);
                }
            }
            else
            {
                ApplyMask();
                write.Append(_buffer);
            }
            return write.FlushAsync();

        }

        internal Message(List<PreservedBuffer> buffers)
        {
            _mask = 0;
            _text = null;
            IsFinal = true;
            //if (buffers.Count == 1) // can simplify
            //{
            //    _buffer = buffers[0];
            //    this._buffers = null;
            //}
            //else
            //{
                _buffer = default(ReadableBuffer);
                this._buffers = buffers;
            //}
        }
        private void ApplyMask()
        {
            if (_mask != 0)
            {
                WebSocketsFrame.ApplyMask(ref _buffer, _mask);
                _mask = 0;
            }
        }
        public override string ToString() => GetText();
        public string GetText()
        {
            if (_text != null) return _text;

            var buffers = this._buffers;
            if (buffers == null)
            {
                if (_buffer.Length == 0) return _text = "";

                ApplyMask();
                return _text = _buffer.GetUtf8String();
            }
            return _text = GetText(buffers);
        }

        private static readonly Encoding Utf8Encoding = Encoding.UTF8;
        private static Decoder Utf8Decoder;

        private static string GetText(List<PreservedBuffer> buffers)
        {
            // try to re-use a shared decoder; note that in heavy usage, we might need to allocate another
            var decoder = (Decoder)Interlocked.Exchange<Decoder>(ref Utf8Decoder, null);
            if (decoder == null) decoder = Utf8Encoding.GetDecoder();
            else decoder.Reset();

            var length = 0;
            foreach (var buffer in buffers) length += buffer.Buffer.Length;

            var capacity = length; // worst case is 1 byte per char
            var chars = new char[capacity];
            var charIndex = 0;

            int bytesUsed = 0;
            int charsUsed = 0;
            bool completed;
            foreach (var buffer in buffers)
            {
                foreach (var span in buffer.Buffer)
                {
                    ArraySegment<byte> segment;
                    if(!span.TryGetArray(out segment))
                    {
                        throw new InvalidOperationException("Array not available for span");
                    }
                    decoder.Convert(
                        segment.Array,
                        segment.Offset,
                        segment.Count,
                        chars,
                        charIndex,
                        capacity,
                        false, // a single character could span two spans
                        out bytesUsed,
                        out charsUsed,
                        out completed);

                    charIndex += charsUsed;
                    capacity -= charsUsed;
                }
            }
            // make the decoder available for re-use
            Interlocked.CompareExchange<Decoder>(ref Utf8Decoder, decoder, null);
            return new string(chars, 0, charIndex);
        }
        private static readonly byte[] NilBytes = new byte[0];
        public byte[] GetBytes()
        {
            int len = GetPayloadLength();
            if (len == 0) return NilBytes;

            ApplyMask();
            return _buffer.ToArray();
        }
        public int GetPayloadLength()
        {
            var buffers = this._buffers;
            if (buffers == null) return _buffer.Length;
            int count = 0;
            foreach (var buffer in buffers) count += buffer.Buffer.Length;
            return count;
        }

        void IMessageWriter.WritePayload(WritableBuffer destination)
        {
            var buffers = this._buffers;
            if (buffers == null)
            {
                ApplyMask();
                destination.Append(_buffer);
            }
            else
            {
                foreach(var buffer in buffers)
                {
                    destination.Append(buffer.Buffer);
                }
            }

        }
    }
}
