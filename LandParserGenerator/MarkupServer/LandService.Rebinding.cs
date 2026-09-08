using System;
using System.Collections.Generic;
using System.Linq;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		private sealed class AnchorContext
		{
			public string Id { get; set; }
			public string Language { get; set; }
			public string Family { get; set; }
			public string AnchorKind { get; set; }
			public string ProfileKey { get; set; }
			public int StartOffset { get; set; }
			public int EndOffset { get; set; }
			public string HeaderCoreNorm { get; set; }
			public string AncestorPathNorm { get; set; }
			public string InnerSketchNorm { get; set; }
			public List<Arg> HeaderArgs { get; set; } = new();
			public int OrdinalInParent { get; set; }
			public Dictionary<string, double> SiblingBag { get; set; }
		}

		private static string InferAnchorFamilyFromKind(string anchorKind)
		{
			if (string.Equals(anchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindTs, StringComparison.OrdinalIgnoreCase))
				return AnchorFamilyCallable;

			if (string.Equals(anchorKind, AnchorKindGqlType, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInput, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(anchorKind, AnchorKindGqlInterface, StringComparison.OrdinalIgnoreCase))
				return AnchorFamilyRecord;

			return null;
		}

		private static string EnsureAnchorFamily(TreeNode node)
		{
			if (node == null)
				return null;

			if (string.IsNullOrWhiteSpace(node.AnchorFamily))
				node.AnchorFamily = InferAnchorFamilyFromKind(node.AnchorKind);

			return node.AnchorFamily;
		}

		private static string GetProfileKey(string language, string family, string anchorKind)
			=> $"{(language ?? "").Trim().ToLowerInvariant()}:{(family ?? "").Trim()}:{(anchorKind ?? "").Trim()}";

		private static string GetProfileKey(TreeNode node)
		{
			if (node == null)
				return null;

			var family = EnsureAnchorFamily(node);
			if (string.IsNullOrWhiteSpace(node.Language) || string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(node.AnchorKind))
				return null;

			return GetProfileKey(node.Language, family, node.AnchorKind);
		}

		private static bool CanParticipateInRebinding(TreeNode node)
		{
			if (node == null || !string.Equals(node.NodeType, "anchor", StringComparison.OrdinalIgnoreCase))
				return false;

			return !string.IsNullOrWhiteSpace(GetProfileKey(node));
		}

		private static Dist.Weights GetWeightsForProfileKey(string profileKey)
		{
			if (string.IsNullOrWhiteSpace(profileKey))
				return new Dist.Weights();

			if (profileKey.Contains($":{AnchorFamilyCallable}:", StringComparison.OrdinalIgnoreCase))
				return new Dist.Weights();

			return new Dist.Weights
			{
				NameW = 0.25,
				ArgsW = 0.00,
				ReturnsW = 0.45,
				ParentW = 0.05,
				NeighW = 0.25,
				NameScale = 8,
				TypeScale = 16,
				ArgNameScale = 8,
				ReturnsScale = 16,
				ReceiverScale = 8,
			};
		}

		private AnchorContext BuildAnchorContext(TreeNode node)
		{
			if (!CanParticipateInRebinding(node))
				return null;

			return new AnchorContext
			{
				Id = node.Id,
				Language = node.Language,
				Family = EnsureAnchorFamily(node),
				AnchorKind = node.AnchorKind,
				ProfileKey = GetProfileKey(node),
				StartOffset = node.StartOffset ?? 0,
				EndOffset = node.EndOffset ?? 0,
				HeaderCoreNorm = node.MethodNameNorm ?? "",
				AncestorPathNorm = node.ParentNameNorm ?? "",
				InnerSketchNorm = node.ReturnTypeNorm ?? "",
				HeaderArgs = (node.Args ?? new List<Arg>())
					.Select(x => new Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
					.ToList(),
				OrdinalInParent = node.OrdinalInParent ?? 0,
				SiblingBag = node.NeighborBag != null ? new Dictionary<string, double>(node.NeighborBag, StringComparer.Ordinal) : null,
			};
		}

		private static MethodAnchor ToLegacyMethodAnchor(AnchorContext ctx)
		{
			if (ctx == null)
				return null;

			return new MethodAnchor
			{
				Id = ctx.Id,
				StartOffset = ctx.StartOffset,
				EndOffset = ctx.EndOffset,
				MethodNameNorm = ctx.HeaderCoreNorm,
				ParentNameNorm = ctx.AncestorPathNorm,
				ReturnTypeNorm = ctx.InnerSketchNorm,
				Args = (ctx.HeaderArgs ?? new List<Arg>())
					.Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm })
					.ToList(),
				OrdinalInParent = ctx.OrdinalInParent,
				NeighborBag = ctx.SiblingBag != null ? new Dictionary<string, double>(ctx.SiblingBag, StringComparer.Ordinal) : null,
			};
		}

		private static double AnchorContextDistance(AnchorContext a, AnchorContext b, Dist.Weights weights)
		{
			return Dist.AnchorDistance(ToLegacyMethodAnchor(a), ToLegacyMethodAnchor(b), weights);
		}

		// =================================================================
		// Правило принятия решения при перепривязке (общее для updateAnchor,
		// перепривязки manual-якорей при рескане и второго слоя сшивки Id).
		//
		//   Accepted  — d1 <= tau и (d2 - d1) / d2 >= margin: уверенная привязка;
		//   Ambiguous — кандидат близко, но второй почти так же близко: решает пользователь;
		//   Lost      — ближайший дальше tau: сущность не найдена.
		//
		// Относительный margin, а не абсолютный: одинаковое разделение кандидатов
		// трактуется одинаково при любой абсолютной близости (аудит 2026-08, §3.8.5).
		// Пороги калибруются по кривой risk–coverage на корпусе реальной истории
		// (experiments/e05); до калибровки — предварительные значения.
		// =================================================================

		internal enum RebindStatus { Accepted, Ambiguous, Lost }

		internal sealed class RebindDecision
		{
			public RebindStatus Status;
			public int BestIndex = -1;
			public double BestDist = double.NaN;
			public double? SecondDist;
			public string Reason = "";
		}

		internal sealed class RebindSettings
		{
			/// <summary>
			/// Порог дистанции для уверенной привязки. Калибровка по кривой risk–coverage на tune-части
			/// корпуса реальной истории (experiments/e05, веса e02/02): tau = 0.30, margin = 0.30 дают
			/// покрытие 98.4 % при доле ошибочных автопривязок 0.08 % (на всём корпусе 99.4 % / 0.03 %).
			/// </summary>
			public double Tau = ReadEnvDouble("LAND_REBIND_TAU", 0.30);
			/// <summary>Минимальный относительный отрыв (d2 - d1) / d2. Без него риск вырастает в 3–4 раза.</summary>
			public double MinRelativeMargin = ReadEnvDouble("LAND_REBIND_MARGIN", 0.30);
			/// <summary>Сколько соседей запрашивать у индекса (нужно ≥ 2 для margin).</summary>
			public int K = 2;

			public static RebindSettings Default => new RebindSettings();

			internal static double ReadEnvDouble(string name, double def)
			{
				var s = Environment.GetEnvironmentVariable(name);
				return !string.IsNullOrWhiteSpace(s)
					&& double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
					? v : def;
			}
		}

		/// <summary>Чистое правило: по двум ближайшим дистанциям выдаёт статус.</summary>
		internal static RebindStatus DecideCore(double d1, double? d2, double tau, double minRelativeMargin, out string reason)
		{
			if (double.IsNaN(d1)) { reason = "no candidates"; return RebindStatus.Lost; }
			if (d1 > tau) { reason = $"d1={d1:F4} > tau={tau:F2}"; return RebindStatus.Lost; }
			if (d2.HasValue)
			{
				var rel = d2.Value <= 0.0 ? 0.0 : (d2.Value - d1) / d2.Value;
				if (rel < minRelativeMargin)
				{
					reason = $"margin={rel:F3} < {minRelativeMargin:F2} (d1={d1:F4}, d2={d2.Value:F4})";
					return RebindStatus.Ambiguous;
				}
			}
			reason = $"d1={d1:F4}" + (d2.HasValue ? $", d2={d2.Value:F4}" : "");
			return RebindStatus.Accepted;
		}

		private static RebindDecision DecideFor(IReadOnlyList<VPTree<AnchorContext>.KNNResult> cands, double tau, double minRelativeMargin)
		{
			var dec = new RebindDecision();
			if (cands == null || cands.Count == 0)
			{
				dec.Status = RebindStatus.Lost;
				dec.Reason = "no candidates";
				return dec;
			}
			dec.BestIndex = cands[0].Index;
			dec.BestDist = cands[0].Dist;
			dec.SecondDist = cands.Count > 1 ? cands[1].Dist : (double?)null;
			dec.Status = DecideCore(dec.BestDist, dec.SecondDist, tau, minRelativeMargin, out var reason);
			dec.Reason = reason;
			return dec;
		}

		// =================================================================
		// Единая реализация NeighborBag (VPTreeLib.AnchorsIO) для сервера и экспериментов.
		// Вид мешка: LAND_NEIGHBOR_BAG = rich (по умолчанию) | server | none.
		// =================================================================

		internal static NeighborBagKind NeighborBagKindSetting = ParseBagKind(Environment.GetEnvironmentVariable("LAND_NEIGHBOR_BAG"));

		private static NeighborBagKind ParseBagKind(string s)
		{
			switch ((s ?? "").Trim().ToLowerInvariant())
			{
				case "server": return NeighborBagKind.ServerName;
				case "none": return NeighborBagKind.None;
				default: return NeighborBagKind.Rich;
			}
		}

		/// <summary>
		/// Мешки соседей в .land/anchors.json не хранятся (AnchorStore, версия 10): после загрузки строим их заново
		/// по тем же группам, что при извлечении (BuildSemanticMarkupFromDisk): GraphQL — только поля (gqlField),
		/// TypeScript — все якоря языка; у остальных (объявления типов) мешка нет. Результат детерминирован:
		/// группировка по (файл, родитель), порядок по OrdinalInParent, IDF по всему набору языка.
		/// </summary>
		internal static void RebuildNeighborBagsForPersistedAnchors(IEnumerable<TreeNode> anchors)
		{
			var list = (anchors ?? Enumerable.Empty<TreeNode>())
				.Where(x => x != null && string.Equals(x.NodeType, "anchor", StringComparison.OrdinalIgnoreCase))
				.ToList();
			foreach (var a in list)
				a.NeighborBag = null;

			var gqlFields = list
				.Where(x => string.Equals(x.Language, LangGql, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(x.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (gqlFields.Count > 0)
				AssignNeighborBags(gqlFields);

			var tsAnchors = list
				.Where(x => string.Equals(x.Language, LangTs, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (tsAnchors.Count > 0)
				AssignNeighborBags(tsAnchors);
		}

		/// <summary>
		/// Строит NeighborBag для набора якорей одного языка. Группировка по (файл, родитель),
		/// IDF — по всему набору, как в экспериментальном корпусе.
		/// </summary>
		private static void AssignNeighborBags(IReadOnlyList<TreeNode> anchors)
		{
			if (anchors == null || anchors.Count == 0)
				return;

			var stubs = new List<MethodAnchor>(anchors.Count);
			for (var i = 0; i < anchors.Count; i++)
			{
				var n = anchors[i];
				stubs.Add(new MethodAnchor
				{
					Id = string.IsNullOrEmpty(n.Id) ? i.ToString() : n.Id,
					File = n.Filepath ?? "",
					MethodNameNorm = n.MethodNameNorm ?? "",
					ParentNameNorm = n.ParentNameNorm ?? "",
					ReturnTypeNorm = n.ReturnTypeNorm ?? "",
					Args = (n.Args ?? new List<Arg>()).Select(a => new MethodAnchor.Arg { TypeNorm = a.TypeNorm ?? "", NameNorm = a.NameNorm ?? "" }).ToList(),
					OrdinalInParent = n.OrdinalInParent ?? 0,
					StartOffset = n.StartOffset ?? 0,
					EndOffset = n.EndOffset ?? 0,
				});
			}

			AnchorsIO.BuildNeighborBags(stubs, NeighborBagKindSetting, window: 4, groupByFile: true);

			for (var i = 0; i < anchors.Count; i++)
				anchors[i].NeighborBag = stubs[i].NeighborBag;
		}
	}
}
