using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPTree
{
	/// <summary>
	/// Способ построения NeighborBag (мешка соседей) — единая реализация для сервера и экспериментов.
	/// </summary>
	public enum NeighborBagKind
	{
		/// <summary>Компонента Neigh не используется (мешки не строятся).</summary>
		None,
		/// <summary>
		/// Ключи fine (имя|тип|типы аргументов), coarse (тип|типы аргументов) и направленные (L/R+смещение),
		/// вес 1/(1+|Δ|)·IDF, окно ±window. То, что измерялось в Python-экспериментах.
		/// </summary>
		Rich,
		/// <summary>
		/// Ключ — нормализованное имя соседа, вес 1/(1+|Δ|), все соседи по родителю без окна.
		/// То, что до сентября 2026 работало в MarkupServer (BuildFieldNeighborBag).
		/// </summary>
		ServerName,
	}

	public static class AnchorsIO
	{
		public static void SaveJson(string path, IEnumerable<MethodAnchor> anchors)
		{
			var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
			File.WriteAllText(path, JsonConvert.SerializeObject(anchors, settings));
		}

		/// <summary>
		/// Loads anchors from JSON. Does NOT build NeighborBag by default (backward-compatible).
		/// </summary>
		public static List<MethodAnchor> LoadJson(string path)
		{
			return LoadJson(path, buildNeighborBags: false, neighborWindow: 4);
		}

		public static List<MethodAnchor> LoadJson(string path, bool buildNeighborBags, int neighborWindow = 4)
		{
			var txt = File.ReadAllText(path);
			var list = JsonConvert.DeserializeObject<List<MethodAnchor>>(txt) ?? new List<MethodAnchor>();

			if (buildNeighborBags)
				BuildNeighborBags(list, NeighborBagKind.Rich, neighborWindow);

			return list;
		}

		/// <summary>Совместимость: прежняя сигнатура строит Rich-мешки.</summary>
		public static void BuildNeighborBags(IList<MethodAnchor> anchors, int window = 4)
		{
			BuildNeighborBags(anchors, NeighborBagKind.Rich, window);
		}

		/// <summary>
		/// Строит NeighborBag для якорей (на месте). Соседи берутся внутри группы
		/// (File, ParentNameNorm) при groupByFile = true, иначе внутри ParentNameNorm.
		/// Порядок внутри группы: OrdinalInParent (если задан хотя бы у одного), иначе StartOffset.
		/// </summary>
		public static void BuildNeighborBags(IList<MethodAnchor> anchors, NeighborBagKind kind, int window = 4, bool groupByFile = true)
		{
			if (anchors == null || anchors.Count == 0) return;
			if (window <= 0) window = 4;

			if (kind == NeighborBagKind.None)
			{
				foreach (var a in anchors) if (a != null) a.NeighborBag = null;
				return;
			}

			// ===== NeighborBag tuning knobs (Rich) =====
			const double CoarseFactor = 0.25;
			const double DirFactor = 0.20;
			const bool UseIdf = true;

			Dictionary<string, double> idf = null;
			if (kind == NeighborBagKind.Rich && UseIdf)
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
					idf[kv.Key] = Math.Log(1.0 + (N / kv.Value)); // smooth IDF
			}

			Func<MethodAnchor, string> groupKey = a => groupByFile
				? (a.File ?? "") + "" + (a.ParentNameNorm ?? "")
				: (a.ParentNameNorm ?? "");

			foreach (var grp in anchors.Where(a => a != null).GroupBy(groupKey, StringComparer.Ordinal))
			{
				var list = grp.ToList();
				if (list.Count <= 1)
				{
					foreach (var a in list) a.NeighborBag = null;
					continue;
				}

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

					if (kind == NeighborBagKind.ServerName)
					{
						for (int j = 0; j < list.Count; j++)
						{
							if (j == i) continue;
							var key = list[j].MethodNameNorm ?? "";
							if (string.IsNullOrWhiteSpace(key)) continue;
							int ad = Math.Abs(i - j);
							double wv = 1.0 / (1.0 + ad);
							if (bag.TryGetValue(key, out double prev)) bag[key] = prev + wv; else bag[key] = wv;
						}
					}
					else
					{
						int from = i - window; if (from < 0) from = 0;
						int to = i + window; if (to >= list.Count) to = list.Count - 1;

						for (int j = from; j <= to; j++)
						{
							if (j == i) continue;

							int delta = j - i;
							int ad = delta >= 0 ? delta : -delta;
							double w = 1.0 / (1.0 + ad);

							var nb = list[j];
							string kFine = Dist.NeighborSigKeyFine(nb);
							string kCoarse = Dist.NeighborSigKeyCoarse(nb);

							double idfFine = 1.0, idfCoarse = 1.0;
							if (idf != null)
							{
								if (idf.TryGetValue(kFine, out double tmpFine)) idfFine = tmpFine;
								if (idf.TryGetValue(kCoarse, out double tmpCoarse)) idfCoarse = tmpCoarse;
							}

							double wFine = w * idfFine;
							if (bag.TryGetValue(kFine, out double prevFine)) bag[kFine] = prevFine + wFine; else bag[kFine] = wFine;

							double wCoarse = (CoarseFactor * w) * idfCoarse;
							if (bag.TryGetValue(kCoarse, out double prevCoarse)) bag[kCoarse] = prevCoarse + wCoarse; else bag[kCoarse] = wCoarse;

							string kDir = (delta < 0 ? "L" : "R") + ad.ToString() + "|" + kFine;
							double wDir = (DirFactor * w) * idfFine;
							if (bag.TryGetValue(kDir, out double prevDir)) bag[kDir] = prevDir + wDir; else bag[kDir] = wDir;
						}
					}

					list[i].NeighborBag = bag.Count == 0 ? null : bag;
				}
			}
		}
	}
}
