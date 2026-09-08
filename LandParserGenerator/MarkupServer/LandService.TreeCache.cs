using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VPTree;

namespace MarkupServer
{
	/// <summary>
	/// Хранение индекса — «вариант C» (markup/experiments/e13_storage_size, §2б):
	///  • мешки соседей в .land/anchors.json не пишутся (AnchorStore, версия 10) и строятся заново при загрузке
	///    (RebuildNeighborBagsForPersistedAnchors — по тем же группам, что при извлечении);
	///  • снимок VP-дерева каждого профиля хранится в .land/vp_index.json как кэш с хэшем входа
	///    (TreeCacheStore); при совпадении хэша дерево восстанавливается (VPTree.FromSnapshot), иначе строится.
	/// Восстановленное дерево даёт те же k-NN, что построенное: проверяется в MarkupServer.Tests (StorageVariantCTests).
	/// </summary>
	public partial class LandService
	{
		/// <summary>Откуда взято дерево профиля при последнем LoadRebindingForestsFromAnchors: "built" | "cache".</summary>
		private readonly Dictionary<string, string> lastForestSource = new(StringComparer.OrdinalIgnoreCase);

		internal IReadOnlyDictionary<string, string> LastForestSource => lastForestSource;

		internal PersistedMarkup CurrentMarkupForTests => currentMarkup;

		/// <summary>
		/// Хэш входа построения дерева: параметры (seed, вид мешков, веса) и все поля контекстов,
		/// участвующие в расстоянии, в порядке списка (порядок влияет на выбор опорных точек).
		/// Id якорей в хэш и в снимок не входят: auto-якоря получают новый GUID при каждом извлечении,
		/// поэтому снимок ссылается на элементы по позиции в списке.
		/// </summary>
		private static string TreeCacheHash(IReadOnlyList<AnchorContext> contexts, Dist.Weights w)
		{
			var sb = new StringBuilder();
			sb.Append("tree-cache-v1|seed=42|bags=").Append(NeighborBagKindSetting).Append('|').Append(DescribeWeights(w)).Append('\n');
			foreach (var c in contexts)
			{
				sb.Append(c.HeaderCoreNorm).Append('\t')
				  .Append(c.AncestorPathNorm).Append('\t')
				  .Append(c.InnerSketchNorm).Append('\t');
				foreach (var a in c.HeaderArgs ?? new List<Arg>())
					sb.Append(a?.TypeNorm).Append('=').Append(a?.NameNorm).Append(';');
				sb.Append('\t').Append(c.OrdinalInParent.ToString(CultureInfo.InvariantCulture)).Append('\t');
				if (c.SiblingBag != null)
				{
					foreach (var kv in c.SiblingBag.OrderBy(k => k.Key, StringComparer.Ordinal))
						sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
				}
				sb.Append('\n');
			}

			using var sha = SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
		}

		private static string DescribeWeights(Dist.Weights w)
		{
			string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);
			return $"w={R(w.NameW)},{R(w.ArgsW)},{R(w.ReturnsW)},{R(w.ParentW)},{R(w.NeighW)}"
				+ $"|scales={w.NameScale},{w.TypeScale},{w.ArgNameScale},{w.ReturnsScale},{w.ReceiverScale}"
				+ $"|argsNorm={w.ArgsNorm}|argMax={w.ArgMaxCount}";
		}

		/// <summary>
		/// Пытается восстановить дерево профиля из снимка. Null при любом несоответствии или ошибке —
		/// тогда дерево строится обычным путём.
		/// </summary>
		private VPTree<AnchorContext> TryRestoreTreeFromCache(PersistedProfileTree cached, List<AnchorContext> contexts,
			Func<AnchorContext, AnchorContext, double> distance, string profileKey)
		{
			try
			{
				var snap = cached?.Snapshot;
				if (snap?.Nodes == null || snap.ItemIds == null
					|| snap.Nodes.Count != contexts.Count || snap.ItemIds.Count != contexts.Count)
					return null;

				// снимок ссылается на элементы по позиции ("0", "1", …) — см. MakeProfileTreeEntry
				for (var i = 0; i < contexts.Count; i++)
					if (!string.Equals(snap.ItemIds[i], i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
						return null;

				return VPTree<AnchorContext>.FromSnapshot(snap, contexts,
					s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 0 && k < contexts.Count ? k : -1,
					distance, 42);
			}
			catch (Exception ex)
			{
				Debug($"[vptree] cache restore failed for profile={profileKey}: {ex.Message}");
				return null;
			}
		}

		private static PersistedProfileTree MakeProfileTreeEntry(VPTree<AnchorContext> tree, List<AnchorContext> contexts, string hash, Dist.Weights w)
		{
			// позиционные идентификаторы: Id auto-якорей меняются при каждом извлечении
			var position = new Dictionary<AnchorContext, string>(contexts.Count, ReferenceEqualityComparer.Instance);
			for (var i = 0; i < contexts.Count; i++)
				position[contexts[i]] = i.ToString(CultureInfo.InvariantCulture);
			var count = contexts.Count;
			return new PersistedProfileTree
			{
				Hash = hash,
				Count = count,
				Weights = DescribeWeights(w),
				BagKind = NeighborBagKindSetting.ToString(),
				Seed = 42,
				BuiltAtUtc = DateTime.UtcNow,
				Snapshot = tree.ToSnapshot(c => position[c], notes: "MarkupServer profile tree cache; items by position; valid only for the hashed input"),
			};
		}

		/// <summary>
		/// Для тестов: строит деревья всех профилей заново и сравнивает k-NN (k = 2) текущих деревьев
		/// (возможно восстановленных из кэша) с построенными для каждого контекста как запроса.
		/// Возвращает число запросов с расхождением.
		/// </summary>
		internal int CountForestMismatchesAgainstFreshBuild()
		{
			var mismatches = 0;
			foreach (var kv in currentTreesByProfileKey)
			{
				if (!currentContextsByProfileKey.TryGetValue(kv.Key, out var contexts) || contexts.Count == 0)
					continue;

				var weights = GetWeightsForProfileKey(kv.Key);
				var fresh = new VPTree<AnchorContext>(contexts, (a, b) => AnchorContextDistance(a, b, weights), 42);
				foreach (var q in contexts)
				{
					var r1 = kv.Value.KNearest(q, 2);
					var r2 = fresh.KNearest(q, 2);
					var same = r1.Count == r2.Count;
					for (var j = 0; same && j < r1.Count; j++)
						same = r1[j].Index == r2[j].Index && r1[j].Dist == r2[j].Dist;
					if (!same)
						mismatches++;
				}
			}
			return mismatches;
		}
	}
}
