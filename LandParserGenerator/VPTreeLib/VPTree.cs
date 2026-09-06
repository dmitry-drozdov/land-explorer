using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenTracing;


namespace VPTree
{
	// =======================
	// VP-Tree (Yianilos 1993), точный kNN при условии, что distance — (псевдо)метрика.
	//
	// Гарантии:
	//  * Отсечение ветвей опирается только на неравенство треугольника, поэтому при
	//    метричной функции расстояния результат KNearest совпадает с полным перебором.
	//  * Порядок среди равных расстояний канонический: (distance, index), где index —
	//    позиция элемента во входном списке. Следовательно результат детерминирован
	//    и не зависит от формы дерева; чтобы он не зависел и от окружения, вызывающая
	//    сторона должна подавать элементы в каноническом порядке (см. CanonicalOrder).
	//  * Построение детерминировано: опорные точки выбираются генератором с фиксированным
	//    seed, распараллеливание затрагивает только вычисление массива расстояний.
	//
	// Обобщённый класс ничего не знает о MethodAnchor: никаких скрытых достроек
	// NeighborBag и подмен запроса здесь нет (раньше были — они делали поведение
	// прототипа и сервера различным).
	// =======================
	public sealed partial class VPTree<T>
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
		private readonly object _rngLock = new object();

		public int BuildDepth { get; private set; }

		/// <summary>Число элементов в индексе.</summary>
		public int Count => _items.Count;

		public VPTree(IList<T> items, Func<T, T, double> distance, int? seed = null, ITracer tracer = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			var idxs = Enumerable.Range(0, _items.Count).ToList();
			using (var scope = tracer?.BuildSpan("Build").StartActive())
			{
				_root = Build(idxs);
				BuildDepth = ComputeDepth(_root);
				if (scope != null)
				{
					scope.Span.SetTag("vptree.build.depth", BuildDepth);
				}
			}
		}

		private Node Build(List<int> idxs)
		{
			return BuildInternal(idxs, parallelDepth: 0);
		}

		private static int ComputeDepth(Node node)
		{
			if (node == null) return 0;
			int ld = ComputeDepth(node.Left);
			int rd = ComputeDepth(node.Right);
			return 1 + (ld > rd ? ld : rd);
		}

		private Node BuildInternal(List<int> idxs, int parallelDepth)
		{
			if (idxs.Count == 0)
				return null;

			var node = new Node();

			int vpPos;
			lock (_rngLock)                       // Random не потокобезопасен
				vpPos = _rng.Next(idxs.Count);

			int vpIndex = idxs[vpPos];
			node.Index = vpIndex;

			if (idxs.Count == 1)
			{
				node.Left = null;
				node.Right = null;
				node.Threshold = 0;
				return node;
			}

			// move vp to end
			int last = idxs.Count - 1;
			int tmp = idxs[vpPos]; idxs[vpPos] = idxs[last]; idxs[last] = tmp;

			int n = idxs.Count - 1;
			var dists = new double[n];

			var vp = _items[vpIndex];
			var items = _items;
			var dist = _dist;

			// Распараллеливаем только вычисление расстояний: результат по индексам
			// детерминирован независимо от числа потоков.
			if (n >= 256)
			{
				var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
				const int chunkSize = 16;
				Parallel.ForEach(Partitioner.Create(0, n, chunkSize), opts, range =>
				{
					for (int i = range.Item1; i < range.Item2; i++)
						dists[i] = dist(vp, items[idxs[i]]);
				});
			}
			else
			{
				for (int i = 0; i < n; i++)
					dists[i] = dist(vp, items[idxs[i]]);
			}

			// median via quickselect (average O(n))
			double mu = QuickSelectMedian(dists);

			// Safety: NaN would completely break the tree/search.
			if (double.IsNaN(mu))
				mu = 0.0;

			node.Threshold = mu;

			// Балансировка при квантованной метрике (много равных расстояний):
			// порог = медиана, но элементы с d == mu делятся между поддеревьями так,
			// чтобы слева оказалось ~n/2. Инварианты для поиска сохраняются:
			// слева d(vp,x) <= mu, справа d(vp,x) >= mu.
			var left = new List<int>(n / 2 + 1);
			var right = new List<int>(n / 2 + 1);
			List<int> eq = null;

			for (int i = 0; i < n; i++)
			{
				double d = dists[i];
				int id = idxs[i];

				if (d < mu) left.Add(id);
				else if (d > mu) right.Add(id);
				else (eq ??= new List<int>()).Add(id);
			}

			int desiredLeft = n / 2;
			if (eq != null && eq.Count > 0)
			{
				int need = desiredLeft - left.Count; // how many from "==" we must move to left
				if (need < 0) need = 0;
				if (need > eq.Count) need = eq.Count;

				for (int i = 0; i < need; i++) left.Add(eq[i]);
				for (int i = need; i < eq.Count; i++) right.Add(eq[i]);
			}

			if (parallelDepth > 0 && left.Count > 0 && right.Count > 0)
			{
				Node leftNode = null, rightNode = null;

				Parallel.Invoke(
				    () => leftNode = BuildInternal(left, parallelDepth - 1),
				    () => rightNode = BuildInternal(right, parallelDepth - 1)
				);

				node.Left = leftNode;
				node.Right = rightNode;
			}
			else
			{
				node.Left = left.Count > 0 ? BuildInternal(left, 0) : null;
				node.Right = right.Count > 0 ? BuildInternal(right, 0) : null;
			}

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
			while (true)
			{
				if (left == right) return a[left];

				double pivot = a[(left + right) >>> 1];

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

		/// <summary>
		/// k ближайших к query элементов в порядке возрастания (distance, index).
		/// При метричной distance результат совпадает с полным перебором, упорядоченным тем же ключом.
		/// </summary>
		public List<KNNResult> KNearest(T query, int k)
		{
			if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
			var heap = new MaxHeap(initialCapacity: k);
			double tau = double.PositiveInfinity;

			SearchKNN(_root, query, k, heap, ref tau);

			int n = heap.Count;
			var arr = new KNNResult[n];
			for (int i = n - 1; i >= 0; --i)
			{
				heap.Pop(out int idx, out double d);
				arr[i] = new KNNResult(idx, d); // заполняем с конца — уже по возрастанию
			}
			return new List<KNNResult>(arr);
		}

		/// <summary>
		/// Полный перебор с тем же каноническим порядком (distance, index). Служит эталоном
		/// для проверки точности индекса и для замеров; индекс для него не нужен.
		/// </summary>
		public static List<KNNResult> BruteForceKNearest(IList<T> items, Func<T, T, double> distance, T query, int k)
		{
			if (items == null) throw new ArgumentNullException(nameof(items));
			if (distance == null) throw new ArgumentNullException(nameof(distance));
			if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));

			var heap = new MaxHeap(initialCapacity: k);
			for (int i = 0; i < items.Count; i++)
			{
				double d = distance(query, items[i]);
				if (heap.Count < k) heap.Push(i, d);
				else if (MaxHeap.Less(d, i, heap.PeekDist(), heap.PeekIndex())) heap.ReplaceTop(i, d);
			}

			int n = heap.Count;
			var arr = new KNNResult[n];
			for (int i = n - 1; i >= 0; --i)
			{
				heap.Pop(out int idx, out double d);
				arr[i] = new KNNResult(idx, d);
			}
			return new List<KNNResult>(arr);
		}

		private void SearchKNN(Node node, T query, int k, MaxHeap heap, ref double tau)
		{
			if (node == null) return;

			var items = _items;
			var distFn = _dist;

			T vp = items[node.Index];
			double dist = distFn(query, vp);

			if (heap.Count < k)
			{
				heap.Push(node.Index, dist);
				if (heap.Count == k) tau = heap.PeekDist();
			}
			else if (MaxHeap.Less(dist, node.Index, heap.PeekDist(), heap.PeekIndex()))
			{
				// Канонический порядок (dist, index): при равной дистанции побеждает меньший индекс.
				heap.ReplaceTop(node.Index, dist);
				tau = heap.PeekDist();
			}

			double mu = node.Threshold;

			Node near, far;
			if (dist < mu) { near = node.Left; far = node.Right; }
			else { near = node.Right; far = node.Left; }

			if (near != null) SearchKNN(near, query, k, heap, ref tau);

			// Отсечение по неравенству треугольника, НЕстрогое и с запасом PruneEps:
			// элементы на расстоянии ровно tau тоже должны быть рассмотрены (канонический
			// порядок при ties), а запас поглощает ошибку округления при вычислении
			// расстояния (сумма произведений double), из-за которой граница |dist - mu| = tau
			// в плавающей арифметике может «промахнуться» на ~1e-16.
			if (far != null)
			{
				if (dist < mu)
				{
					// слева от порога: |dist - mu| <= tau  <=>  dist + tau >= mu
					if (dist + tau + PruneEps >= mu) SearchKNN(far, query, k, heap, ref tau);
				}
				else
				{
					// справа: dist - tau <= mu
					if (dist - tau - PruneEps <= mu) SearchKNN(far, query, k, heap, ref tau);
				}
			}
		}

		/// <summary>Запас на ошибку округления при отсечении ветвей (расстояния лежат в [0,1]).</summary>
		public const double PruneEps = 1e-9;

		// Max-heap by (distance, index): наверху «худший» из текущих k.
		private sealed class MaxHeap
		{
			private struct Item { public int Index; public double Dist; }

			private readonly List<Item> _a;

			public MaxHeap(int initialCapacity = 0)
			{
				_a = initialCapacity > 0 ? new List<Item>(initialCapacity) : new List<Item>();
			}

			public int Count => _a.Count;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public double PeekDist() => _a[0].Dist;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public int PeekIndex() => _a[0].Index;

			/// <summary>(d1, i1) &lt; (d2, i2) в каноническом порядке.</summary>
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public static bool Less(double d1, int i1, double d2, int i2)
			{
				return d1 < d2 || (d1 == d2 && i1 < i2);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void Push(int index, double dist)
			{
				_a.Add(new Item { Index = index, Dist = dist });
				SiftUp(_a.Count - 1);
			}

			public void Pop(out int index, out double dist)
			{
				var root = _a[0];
				int lastIdx = _a.Count - 1;
				var last = _a[lastIdx];
				_a.RemoveAt(lastIdx);
				if (_a.Count > 0) { _a[0] = last; SiftDown(0); }
				index = root.Index;
				dist = root.Dist;
			}

			public void ReplaceTop(int index, double dist)
			{
				_a[0] = new Item { Index = index, Dist = dist };
				SiftDown(0);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
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

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
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

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			private static int Compare(Item a, Item b)
			{
				int c = a.Dist.CompareTo(b.Dist);
				return c != 0 ? c : a.Index.CompareTo(b.Index);
			}
		}
	}

	/// <summary>
	/// Канонический порядок якорей перед построением индекса: делает результат
	/// независимым от порядка обхода файловой системы и от среды.
	/// </summary>
	public static class CanonicalOrder
	{
		public static List<MethodAnchor> Sort(IEnumerable<MethodAnchor> anchors)
		{
			return (anchors ?? Enumerable.Empty<MethodAnchor>())
				.OrderBy(a => a?.ParentNameNorm ?? "", StringComparer.Ordinal)
				.ThenBy(a => a?.MethodNameNorm ?? "", StringComparer.Ordinal)
				.ThenBy(a => a?.ReturnTypeNorm ?? "", StringComparer.Ordinal)
				.ThenBy(a => a?.StartOffset ?? 0)
				.ThenBy(a => a?.Id ?? "", StringComparer.Ordinal)
				.ToList();
		}
	}
}
