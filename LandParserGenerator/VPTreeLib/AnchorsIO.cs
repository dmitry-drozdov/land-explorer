using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPTree
{
	public static class AnchorsIO
	{
		public static void SaveJson(string path, IEnumerable<MethodAnchor> anchors)
		{
			var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
			File.WriteAllText(path, JsonConvert.SerializeObject(anchors, settings));
		}

		/// <summary>
		/// Loads anchors from JSON. Does NOT build NeighborBag by default (backward-compatible).
		/// Use the overload with buildNeighborBags=true to enable Neigh component in the metric.
		/// </summary>
		public static List<MethodAnchor> LoadJson(string path)
		{
			return LoadJson(path, buildNeighborBags: false, neighborWindow: 4);
		}

		/// <summary>
		/// Loads anchors from JSON and optionally builds NeighborBag for each anchor.
		/// NeighborBag is built inside each ParentNameNorm group, using OrdinalInParent if present,
		/// otherwise StartOffset ordering.
		/// </summary>
		public static List<MethodAnchor> LoadJson(string path, bool buildNeighborBags, int neighborWindow = 4)
		{
			var txt = File.ReadAllText(path);
			var list = JsonConvert.DeserializeObject<List<MethodAnchor>>(txt) ?? new List<MethodAnchor>();

			if (buildNeighborBags)
				BuildNeighborBags(list, neighborWindow);

			return list;
		}

		/// <summary>
		/// Builds NeighborBag for anchors (in-place).
		/// Neighbors are collected within a window around each anchor in the same ParentNameNorm.
		/// Weight is 1/(1+distanceInOrder).
		/// </summary>
		public static void BuildNeighborBags(IList<MethodAnchor> anchors, int window = 4)
		{
			if (anchors == null || anchors.Count == 0) return;
			if (window <= 0) window = 4;

			// ===== NeighborBag tuning knobs =====
			// coarse keys keep overlap between different names; too high -> "same for everyone"
			const double CoarseFactor = 0.25;
			// directional fine keys add ordering signal; too high -> overlap collapses
			const double DirFactor = 0.20;
			const bool UseIdf = true;

			// --- Build global DF/IDF for neighbor signature keys (reduces dominance of ubiquitous signatures like "void|") ---
			Dictionary<string, double> idf = null;
			if (UseIdf)
			{
				var df = new Dictionary<string, int>(StringComparer.Ordinal);
				int totalAnchors = 0;

				for (int i = 0; i < anchors.Count; i++)
				{
					var a = anchors[i];
					if (a == null) continue;
					totalAnchors++;

					string kFine = Dist.NeighborSigKeyFine(a);
					if (df.TryGetValue(kFine, out int c1)) df[kFine] = c1 + 1; else df[kFine] = 1;

					string kCoarse = Dist.NeighborSigKeyCoarse(a);
					if (df.TryGetValue(kCoarse, out int c2)) df[kCoarse] = c2 + 1; else df[kCoarse] = 1;
				}

				idf = new Dictionary<string, double>(df.Count, StringComparer.Ordinal);
				double N = Math.Max(1.0, (double)totalAnchors);
				foreach (var kv in df)
				{
					// Smooth IDF: log(1 + N/df)
					idf[kv.Key] = Math.Log(1.0 + (N / kv.Value));
				}
			}

			// group by parent type
			foreach (var grp in anchors.Where(a => a != null).GroupBy(a => a.ParentNameNorm ?? "", StringComparer.Ordinal))
			{
				var list = grp.ToList();
				if (list.Count <= 1) continue;

				bool hasOrdinal = false;
				for (int i = 0; i < list.Count; i++)
				{
					if (list[i].OrdinalInParent != 0) { hasOrdinal = true; break; }
				}

				list.Sort((x, y) =>
				{
					int kx = hasOrdinal ? x.OrdinalInParent : x.StartOffset;
					int ky = hasOrdinal ? y.OrdinalInParent : y.StartOffset;
					int c = kx.CompareTo(ky);
					if (c != 0) return c;
					c = x.EndOffset.CompareTo(y.EndOffset);
					if (c != 0) return c;
					return string.CompareOrdinal(x.Id ?? "", y.Id ?? "");
				});

				for (int i = 0; i < list.Count; i++)
				{
					var bag = new Dictionary<string, double>(StringComparer.Ordinal);

					int from = i - window; if (from < 0) from = 0;
					int to = i + window; if (to >= list.Count) to = list.Count - 1;

					for (int j = from; j <= to; j++)
					{
						if (j == i) continue;

						int delta = j - i;
						int ad = delta >= 0 ? delta : -delta;

						// base weight: closer neighbors contribute more
						double w = 1.0 / (1.0 + ad);

						var nb = list[j];

						string kFine = Dist.NeighborSigKeyFine(nb);
						string kCoarse = Dist.NeighborSigKeyCoarse(nb);

						double idfFine = 1.0;
						double idfCoarse = 1.0;
						if (idf != null)
						{
							if (idf.TryGetValue(kFine, out double tmpFine)) idfFine = tmpFine;
							if (idf.TryGetValue(kCoarse, out double tmpCoarse)) idfCoarse = tmpCoarse;
						}

						// fine key is the main signal
						double wFine = w * idfFine;
						if (bag.TryGetValue(kFine, out double prevFine))
							bag[kFine] = prevFine + wFine;
						else
							bag[kFine] = wFine;

						// coarse key: keeps overlap across different names
						double wCoarse = (CoarseFactor * w) * idfCoarse;
						if (bag.TryGetValue(kCoarse, out double prevCoarse))
							bag[kCoarse] = prevCoarse + wCoarse;
						else
							bag[kCoarse] = wCoarse;

						// directional fine key: adds ordering signal (small weight)
						string kDir = (delta < 0 ? "L" : "R") + ad.ToString() + "|" + kFine;
						double wDir = (DirFactor * w) * idfFine;
						if (bag.TryGetValue(kDir, out double prevDir))
							bag[kDir] = prevDir + wDir;
						else
							bag[kDir] = wDir;
					}

					// store null instead of empty to keep WeightedJaccard fast
					list[i].NeighborBag = bag.Count == 0 ? null : bag;
				}
			}
		}
	}
}
