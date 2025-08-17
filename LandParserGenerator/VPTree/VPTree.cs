using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	// =======================
	// VP-Tree (exact kNN)
	// =======================
	public sealed class VPTree<T>
	{
		private sealed class Node
		{
			public int Index;
			public double Threshold;
			public Node Left;
			public Node Right;
		}

		private readonly List<T> _items;
		private readonly Func<T, T, double> _dist;
		private readonly Node _root;
		private readonly Random _rng;

		public VPTree(IList<T> items, Func<T, T, double> distance, int? seed = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			var idxs = Enumerable.Range(0, _items.Count).ToArray();
			_root = Build(idxs);
		}

		private Node Build(int[] idxs)
		{
			if (idxs.Length == 0) return null;
			var node = new Node();

			int vpPos = _rng.Next(idxs.Length);
			int vpIndex = idxs[vpPos];
			node.Index = vpIndex;

			if (idxs.Length == 1)
			{
				node.Left = null; node.Right = null; node.Threshold = 0;
				return node;
			}

			// move vp to end
			int last = idxs.Length - 1;
			int tmp = idxs[vpPos]; idxs[vpPos] = idxs[last]; idxs[last] = tmp;

			int n = idxs.Length - 1;
			var dists = new double[n];
			for (int i = 0; i < n; i++)
				dists[i] = _dist(_items[vpIndex], _items[idxs[i]]);

			// median via quickselect (average O(n))
			double mu = QuickSelectMedian(dists);
			node.Threshold = mu;

			var left = new List<int>(n);
			var right = new List<int>(n);
			for (int i = 0; i < n; i++)
			{
				if (dists[i] <= mu) left.Add(idxs[i]);
				else right.Add(idxs[i]);
			}

			node.Left = Build(left.ToArray());
			node.Right = Build(right.ToArray());
			return node;
		}

		private double QuickSelectMedian(double[] arr)
		{
			int n = arr.Length;
			if (n == 0) return 0.0;
			int k = n / 2;
			var a = new double[n];
			Array.Copy(arr, a, n);
			return QuickSelect(a, 0, n - 1, k);
		}

		private double QuickSelect(double[] a, int left, int right, int k)
		{
			var rnd = _rng;
			while (true)
			{
				if (left == right) return a[left];
				int pivotIndex = left + rnd.Next(right - left + 1);
				double pivot = a[pivotIndex];

				int i = left, j = right;
				while (i <= j)
				{
					while (a[i] < pivot) i++;
					while (a[j] > pivot) j--;
					if (i <= j)
					{
						double t = a[i]; a[i] = a[j]; a[j] = t;
						i++; j--;
					}
				}

				if (k <= j) right = j;
				else if (k >= i) left = i;
				else return a[k];
			}
		}

		public sealed class KNNResult
		{
			public int Index;
			public double Dist;
			public KNNResult(int index, double dist) { Index = index; Dist = dist; }
		}

		public List<KNNResult> KNearest(T query, int k)
		{
			if (k <= 0) throw new ArgumentOutOfRangeException("k");
			var heap = new MaxHeap();
			double tau = double.PositiveInfinity;
			SearchKNN(_root, query, k, heap, ref tau);

			var res = new List<KNNResult>(heap.Count);
			while (heap.Count > 0)
			{
				heap.Pop(out int idx, out double d);
				res.Add(new KNNResult(idx, d));
			}
			res.Reverse();
			return res;
		}

		private void SearchKNN(Node node, T query, int k, MaxHeap heap, ref double tau)
		{
			if (node == null) return;

			T vp = _items[node.Index];
			double dist = _dist(query, vp);

			if (heap.Count < k)
			{
				heap.Push(node.Index, dist);
				if (heap.Count == k) tau = heap.PeekDist();
			}
			else if (dist < tau)
			{
				heap.ReplaceTop(node.Index, dist);
				tau = heap.PeekDist();
			}

			double mu = node.Threshold;
			Node first = dist < mu ? node.Left : node.Right;
			Node second = dist < mu ? node.Right : node.Left;

			if (first != null) SearchKNN(first, query, k, heap, ref tau);

			if (second != null)
			{
				if (Math.Abs(dist - mu) <= tau)
					SearchKNN(second, query, k, heap, ref tau);
			}
		}

		// Max-heap by distance (largest on top)
		// Max-heap by distance (largest on top)
		private sealed class MaxHeap
		{
			private struct Item
			{
				public int Index;
				public double Dist;
			}

			private readonly List<Item> _a = new List<Item>();

			public int Count { get { return _a.Count; } }

			public double PeekDist()
			{
				return _a[0].Dist;
			}

			public void Push(int index, double dist)
			{
				var it = new Item { Index = index, Dist = dist };
				_a.Add(it);
				SiftUp(_a.Count - 1);
			}

			// Pop max into out params
			public void Pop(out int index, out double dist)
			{
				var root = _a[0];
				var last = _a[_a.Count - 1];
				_a.RemoveAt(_a.Count - 1);
				if (_a.Count > 0) { _a[0] = last; SiftDown(0); }
				index = root.Index;
				dist = root.Dist;
			}

			public void ReplaceTop(int index, double dist)
			{
				_a[0] = new Item { Index = index, Dist = dist };
				SiftDown(0);
			}

			private void SiftUp(int i)
			{
				while (i > 0)
				{
					int p = (i - 1) / 2;
					if (Compare(_a[p], _a[i]) >= 0) break;
					var t = _a[p]; _a[p] = _a[i]; _a[i] = t;
					i = p;
				}
			}

			private void SiftDown(int i)
			{
				int n = _a.Count;
				while (true)
				{
					int l = 2 * i + 1, r = l + 1, best = i;
					if (l < n && Compare(_a[l], _a[best]) > 0) best = l;
					if (r < n && Compare(_a[r], _a[best]) > 0) best = r;
					if (best == i) break;
					var t = _a[i]; _a[i] = _a[best]; _a[best] = t;
					i = best;
				}
			}

			private static int Compare(Item a, Item b)
			{
				int c = a.Dist.CompareTo(b.Dist);
				if (c != 0) return c;
				return a.Index.CompareTo(b.Index);
			}
		}
	}
}
