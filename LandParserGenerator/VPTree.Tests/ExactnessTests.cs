using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;

namespace Tests
{
	/// <summary>
	/// Точность и детерминизм VP-дерева на реальном корпусе:
	///  * KNearest даёт ровно тот же список (index, dist), что полный перебор с каноническим порядком;
	///  * результат не зависит от порядка подачи элементов после CanonicalOrder.Sort и от повторного построения.
	/// </summary>
	[TestClass]
	public class ExactnessTests
	{
		private static List<MethodAnchor> PrepareCorpus()
		{
			var corpus = CanonicalOrder.Sort(MetricAxiomTests.LoadCorpus());
			AnchorsIO.BuildNeighborBags(corpus, NeighborBagKind.Rich, window: 4);
			return corpus;
		}

		/// <summary>Запросы: копии реальных якорей с небольшими правками (имя, аргументы, тип), чтобы совпадений «в ноль» было мало.</summary>
		internal static List<MethodAnchor> MakeQueries(List<MethodAnchor> corpus, int count, int seed)
		{
			var rng = new Random(seed);
			var res = new List<MethodAnchor>(count);
			for (int i = 0; i < count; i++)
			{
				var src = corpus[rng.Next(corpus.Count)];
				var q = new MethodAnchor
				{
					Id = "q" + i,
					MethodNameNorm = src.MethodNameNorm,
					ParentNameNorm = src.ParentNameNorm,
					ReturnTypeNorm = src.ReturnTypeNorm,
					Args = (src.Args ?? new List<MethodAnchor.Arg>()).Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
					OrdinalInParent = src.OrdinalInParent,
					NeighborBag = src.NeighborBag,
				};
				switch (rng.Next(5))
				{
					case 0: q.MethodNameNorm = q.MethodNameNorm + "x"; break;
					case 1: q.ReturnTypeNorm = q.ReturnTypeNorm + "!"; break;
					case 2: q.Args.Add(new MethodAnchor.Arg { TypeNorm = "Int", NameNorm = "limit" }); break;
					case 3: if (q.Args.Count > 0) q.Args.RemoveAt(0); else q.ParentNameNorm = q.ParentNameNorm + " v2"; break;
					default: break; // без изменений — проверяем ties
				}
				res.Add(q);
			}
			return res;
		}

		private static void AssertSameResults(List<VPTree<MethodAnchor>.KNNResult> vp, List<VPTree<MethodAnchor>.KNNResult> bf, string what)
		{
			Assert.AreEqual(bf.Count, vp.Count, what + ": разный размер результата");
			for (int i = 0; i < bf.Count; i++)
			{
				Assert.AreEqual(bf[i].Index, vp[i].Index, $"{what}: ранг {i}: индекс VP={vp[i].Index} BF={bf[i].Index} (dist VP={vp[i].Dist:R} BF={bf[i].Dist:R})");
				Assert.AreEqual(bf[i].Dist, vp[i].Dist, 0.0, $"{what}: ранг {i}: расстояние");
			}
		}

		[TestMethod]
		[TestCategory("VPTree/Exactness")]
		public void KNearest_EqualsBruteForce_OnRealCorpus_ForSeveralK()
		{
			var corpus = PrepareCorpus();
			var w = new Dist.Weights();
			Func<MethodAnchor, MethodAnchor, double> dist = (a, b) => Dist.AnchorDistance(a, b, w);
			var tree = new VPTree<MethodAnchor>(corpus, dist, 42);
			var queries = MakeQueries(corpus, 300, seed: 1);

			foreach (int k in new[] { 1, 5, 20 })
			{
				int i = 0;
				foreach (var q in queries)
				{
					var vp = tree.KNearest(q, k);
					var bf = VPTree<MethodAnchor>.BruteForceKNearest(corpus, dist, q, k);
					AssertSameResults(vp, bf, $"k={k}, query #{i}");
					i++;
				}
			}
		}

		[TestMethod]
		[TestCategory("VPTree/Exactness")]
		public void KNearest_LegacyArgsNormalization_ReportsMismatches()
		{
			// Документирующий тест: в режиме Legacy (не метрика) дерево МОЖЕТ расходиться с перебором.
			// Здесь не утверждаем, что расхождения обязаны быть (на GraphQL их мало), а только считаем и печатаем.
			var corpus = PrepareCorpus();
			var w = new Dist.Weights { ArgsNorm = Dist.ArgsNormalization.Legacy };
			Func<MethodAnchor, MethodAnchor, double> dist = (a, b) => Dist.AnchorDistance(a, b, w);
			var tree = new VPTree<MethodAnchor>(corpus, dist, 42);
			var queries = MakeQueries(corpus, 300, seed: 1);
			int mismatches = 0;
			foreach (var q in queries)
			{
				var vp = tree.KNearest(q, 5);
				var bf = VPTree<MethodAnchor>.BruteForceKNearest(corpus, dist, q, 5);
				if (vp.Count != bf.Count || vp.Zip(bf, (x, y) => x.Index != y.Index).Any(x => x)) mismatches++;
			}
			Console.WriteLine($"legacy normalization: VP != BF in {mismatches}/{queries.Count} queries (k=5)");
		}

		[TestMethod]
		[TestCategory("VPTree/Determinism")]
		public void Build_IsDeterministic_AfterCanonicalOrdering()
		{
			var corpus = PrepareCorpus();
			var w = new Dist.Weights();
			Func<MethodAnchor, MethodAnchor, double> dist = (a, b) => Dist.AnchorDistance(a, b, w);
			var queries = MakeQueries(corpus, 100, seed: 2);

			// Эталон
			var tree0 = new VPTree<MethodAnchor>(corpus, dist, 42);
			var snap0 = tree0.ToSnapshot(a => a.Id);
			var ref0 = queries.Select(q => tree0.KNearest(q, 5)).ToList();

			var rng = new Random(123);
			for (int rep = 0; rep < 5; rep++)
			{
				// перемешиваем вход, затем канонизируем — дерево и ответы обязаны совпасть
				var shuffled = corpus.OrderBy(_ => rng.Next()).ToList();
				var canon = CanonicalOrder.Sort(shuffled);
				CollectionAssert.AreEqual(corpus.Select(a => a.Id).ToList(), canon.Select(a => a.Id).ToList(), "CanonicalOrder не восстанавливает порядок");

				var tree = new VPTree<MethodAnchor>(canon, dist, 42);
				var snap = tree.ToSnapshot(a => a.Id);
				Assert.AreEqual(snap0.Nodes.Count, snap.Nodes.Count);
				for (int i = 0; i < snap.Nodes.Count; i++)
				{
					Assert.AreEqual(snap0.Nodes[i].PivotId, snap.Nodes[i].PivotId, $"rep {rep}: структура дерева отличается в узле {i}");
					Assert.AreEqual(snap0.Nodes[i].Threshold, snap.Nodes[i].Threshold, 0.0);
				}
				for (int qi = 0; qi < queries.Count; qi++)
					AssertSameResults(tree.KNearest(queries[qi], 5), ref0[qi], $"rep {rep}, query {qi}");
			}
		}
	}
}
