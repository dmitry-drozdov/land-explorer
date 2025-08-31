using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;

namespace VPTree
{
	[TestClass]
	public class VpTreeSnapshotTests
	{
		// ---------- helpers ----------

		private static Tuple<string, string> Arg(string type, string name) => Tuple.Create(type, name);

		private static MethodAnchor A(
		    string id, string parent, string name,
		    Tuple<string, string>[] args, string[] rets,
		    int start = 0, int end = 0)
		{
			return MethodAnchor.FromRaw(
			    id,
			    start, end,
			    name,
			    args ?? new Tuple<string, string>[0],
			    rets ?? new string[0],
			    parent
			);
		}

		/// <summary>
		/// Строим мешки соседей по группам родителя (порядок как в списке).
		/// </summary>
		private static void BuildNeighborBagsForCorpus(IList<MethodAnchor> anchors, int window = 1)
		{
			foreach (var grp in anchors.GroupBy(a => a.ParentNameNorm, StringComparer.Ordinal))
			{
				var list = grp.ToList();
				// назначим порядковые номера в текущем порядке
				for (int i = 0; i < list.Count; i++) list[i].OrdinalInParent = i + 1;
			}
		}

		private static Dist.Weights WBase(double nameW = 0.25, double argsW = 0.40, double returnsW = 0.20, double parentW = 0.20, double neighW = 0.0)
		    => new Dist.Weights
		    {
			    NameW = nameW,
			    ArgsW = argsW,
			    ReturnsW = returnsW,
			    ParentW = parentW,
			    NeighW = 0,
			    NameScale = 8,
			    TypeScale = 16,
			    ArgNameScale = 8,
			    ReturnsScale = 16,
			    ReceiverScale = 8
		    };


		private static List<VPTree<MethodAnchor>.KNNResult> RunKnn(VPTree<MethodAnchor> tree, IList<MethodAnchor> corpus, MethodAnchor q, int k)
		{
			var res = tree.KNearest(q, k);
			// sanity: отсортировано по возрастанию
			for (int i = 1; i < res.Count; i++)
				Assert.IsTrue(res[i - 1].Dist <= res[i].Dist + 1e-12, "kNN must be sorted by distance");
			return res;
		}

		private static void AssertKnnEqual(
		    IList<VPTree<MethodAnchor>.KNNResult> a, IList<VPTree<MethodAnchor>.KNNResult> b,
		    IList<MethodAnchor> corpus, double eps = 1e-12)
		{
			Assert.AreEqual(a.Count, b.Count, "Different kNN list sizes");
			for (int i = 0; i < a.Count; i++)
			{
				var idA = corpus[a[i].Index].Id;
				var idB = corpus[b[i].Index].Id;
				Assert.AreEqual(idA, idB, $"Different candidate at rank {i}");
				Assert.AreEqual(a[i].Dist, b[i].Dist, eps, $"Different distance at rank {i}");
			}
		}

		// ---------- tests ----------

		[TestMethod]
		[TestCategory("VPTree/Snapshot")]
		public void Snapshot_RoundTrip_PreservesKnn_WithoutNeighbors()
		{
			// Corpus (без упора на соседей)
			var anchors = new List<MethodAnchor>
			    {
				A("old#1","Hasher","ComputeHash",
				    new[]{ Arg("int","a"), Arg("string","b") }, new[]{ "int" }),
				A("old#2","Repo","FindUser",
				    new[]{ Arg("int","id") }, new[]{ "*User","error" }),
				A("old#3","Encoder","WriteJSON",
				    new[]{ Arg("io.Writer","w"), Arg("any","v"), Arg("bool","indent") }, new[]{ "error" }),
				A("old#4","Math","Add",
				    new[]{ Arg("int","x"), Arg("int","y") }, new[]{ "int" }),
			    };

			// Можно построить мешки, но в этом тесте NeighW=0, поэтому не обязательно
			BuildNeighborBagsForCorpus(anchors, window: 1);

			var w = WBase(neighW: 0.0);
			Func<MethodAnchor, MethodAnchor, double> dist = (x, y) => Dist.AnchorDistance(x, y, w);

			// Build tree
			var tree = new VPTree<MethodAnchor>(anchors, dist);

			// Queries
			var q1 = A("q#1", "Hasher", "FindUser",
			    new[] { Arg("int", "a"), Arg("string", "b") }, new[] { "int" });
			var q2 = A("q#2", "Repo", "ComputeHash",
			    new[] { Arg("int", "id") }, new[] { "*User", "error" });

			var pre1 = RunKnn(tree, anchors, q1, 3);
			var pre2 = RunKnn(tree, anchors, q2, 3);

			// Save snapshot
			var snap = tree.ToSnapshot(a => a.Id, notes: "test:base");
			var path = Path.Combine(Path.GetTempPath(), "vp_" + Guid.NewGuid().ToString("N") + ".json");
			VpTreeStorage.SaveJson(path, snap);
			Assert.IsTrue(File.Exists(path), "Snapshot file was not created.");

			try
			{
				// Load snapshot into new tree
				var id2idx = anchors.Select((a, i) => new { a.Id, i })
						    .ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
				int IndexOfId(string id) => id2idx.ContainsKey(id) ? id2idx[id] : -1;

				var snapLoaded = VpTreeStorage.LoadJson(path);
				var tree2 = VPTree<MethodAnchor>.FromSnapshot(snapLoaded, anchors, IndexOfId, dist);

				var post1 = RunKnn(tree2, anchors, q1, 3);
				var post2 = RunKnn(tree2, anchors, q2, 3);

				// Compare
				AssertKnnEqual(pre1, post1, anchors);
				AssertKnnEqual(pre2, post2, anchors);
			}
			finally
			{
				try { File.Delete(path); } catch { /* ignore */ }
			}
		}
	}
}
