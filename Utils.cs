using System;

namespace IngameScript
{
    /// <summary>
    /// 定容环形 FIFO 队列：O(1) 入队/出队，容量固定。
    /// </summary>
    public class RingQueue<T>
    {
        readonly T[] _items;
        int _head, _tail;

        public RingQueue(int capacity)
        {
            _items = new T[capacity + 1];  // +1 用于区分空/满
        }

        public bool TryEnqueue(T item)
        {
            int next = (_tail + 1) % _items.Length;
            if (next == _head) return false;
            _items[_tail] = item;
            _tail = next;
            return true;
        }

        public bool TryDequeue(out T item)
        {
            if (_head == _tail) { item = default(T); return false; }
            item = _items[_head];
            _head = (_head + 1) % _items.Length;
            return true;
        }

        public int Count
        {
            get { return (_tail - _head + _items.Length) % _items.Length; }
        }

        public void Clear() { _head = 0; _tail = 0; }
    }
}
