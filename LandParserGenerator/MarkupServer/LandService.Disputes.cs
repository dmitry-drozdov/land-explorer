using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	/// <summary>
	/// Спорные компоненты перепривязки и проценты для пользователя.
	///
	/// При полном рескане шаг назначения (сшивка Id, слой 2) решает компоненты точек, претендующих на одних и тех же
	/// кандидатов. Компонента, где хотя бы одна точка неоднозначна, сохраняется в снимке (PersistedMarkup.Disputes),
	/// если у какой-нибудь её непринятой точки есть данные пользователя: членство в папке, пользовательская связь,
	/// комментарий или сама точка ручная. Принятые точки остаются применёнными; непринятые с данными — в _Lost.
	///
	/// Окно плагина запрашивает проценты (land/getDispute) при каждом выборе пользователя и применяет итоговую
	/// расстановку (land/resolveDispute). Проценты — распределение Гиббса по ценам расстановок
	/// (VPTreeLib.AssignmentProbabilities): процент варианта — вероятность, что верны все строки сразу; процент строки —
	/// сумма процентов вариантов, где точка переносится именно туда; в строке, выбранной пользователем, — без её выбора.
	/// </summary>
	public partial class LandService
	{
		private const string DisputeAccepted = "accepted";
		private const string DisputeAmbiguous = "ambiguous";
		private const string DisputeLost = "lost";

		// ================================================================ рескан: сборка спорных компонент

		private List<DisputedComponent> BuildDisputesAfterRescan(
			PersistedMarkup previousMarkup,
			StitchResult stitch,
			List<(TreeNode Manual, List<(TreeNode Node, double Dist)> Candidates)> ambiguousManual,
			HashSet<string> validAnchorIds,
			HashSet<string> lostAnchorIds,
			ICollection<string> focusIds = null)
		{
			var focus = new HashSet<string>(focusIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
			var memberIds = new HashSet<string>(
				(previousMarkup?.Memberships ?? new List<UserGroupMembership>())
					.Where(m => !string.IsNullOrWhiteSpace(m?.AnchorId)).Select(m => m.AnchorId),
				StringComparer.OrdinalIgnoreCase);
			var linkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var l in previousMarkup?.Links ?? new List<AnchorLink>())
			{
				if (l == null || IsAutoPair(l)) continue;
				if (!string.IsNullOrWhiteSpace(l.SourceAnchorId)) linkIds.Add(l.SourceAnchorId);
				if (!string.IsNullOrWhiteSpace(l.TargetAnchorId)) linkIds.Add(l.TargetAnchorId);
			}
			bool HasUserData(TreeNode a) => a != null
				&& (a.IsManual || !string.IsNullOrWhiteSpace(a.Comment) || memberIds.Contains(a.Id) || linkIds.Contains(a.Id));

			var now = DateTime.UtcNow;
			var result = new List<DisputedComponent>();

			foreach (var sd in stitch?.Disputes ?? new List<StitchDispute>())
			{
				var points = sd.Points.Select(pt =>
				{
					var dp = SnapshotDisputedPoint(pt.Prev);
					dp.HasUserData = HasUserData(pt.Prev);
					dp.Status = pt.Status switch
					{
						AssignmentRebind.Status.Accepted => DisputeAccepted,
						AssignmentRebind.Status.Ambiguous => DisputeAmbiguous,
						_ => DisputeLost,
					};
					dp.AppliedFreshId = pt.AppliedFreshId;
					dp.Candidates = pt.Candidates
						.Where(c => c.Fresh != null && validAnchorIds.Contains(c.Fresh.Id))
						.Select(c => new DisputeCandidate { AnchorId = c.Fresh.Id, Dist = c.Dist })
						.ToList();
					return dp;
				}).ToList();

				// Спрашиваем пользователя, только если под вопросом его данные (принятая точка уже применена уверенно)
				// или он сам попросил перепривязать эту точку (Update Anchor).
				if (!points.Any(x => x.Status != DisputeAccepted && (x.HasUserData || focus.Contains(x.AnchorId))))
					continue;
				result.Add(new DisputedComponent { Id = MakeDisputeId(points), CreatedAtUtc = now, RejectCost = sd.RejectCost, Points = points });
			}

			var rs = RebindSettings.Default;
			foreach (var (manual, cands) in ambiguousManual ?? new List<(TreeNode, List<(TreeNode, double)>)>())
			{
				var dp = SnapshotDisputedPoint(manual);
				dp.IsManual = true;
				dp.HasUserData = true;
				dp.Status = DisputeAmbiguous;
				dp.Candidates = (cands ?? new List<(TreeNode Node, double Dist)>())
					.Where(c => c.Node != null && validAnchorIds.Contains(c.Node.Id))
					.Select(c => new DisputeCandidate { AnchorId = c.Node.Id, Dist = c.Dist })
					.ToList();
				if (dp.Candidates.Count == 0)
					continue;
				result.Add(new DisputedComponent
				{
					Id = MakeDisputeId(new[] { dp }),
					CreatedAtUtc = now,
					RejectCost = rs.RejectCost,
					Points = new List<DisputedPoint> { dp },
				});
			}

			// Нерешённые компоненты прошлого рескана: кандидаты, которых больше нет, отбрасываются; компонента остаётся,
			// пока у какой-нибудь её непринятой точки с данными пользователя есть кандидаты. Если точка попала в новую
			// компоненту, старая снимается — новые расстояния точнее.
			var freshPointIds = new HashSet<string>(result.SelectMany(d => d.Points).Select(x => x.AnchorId), StringComparer.OrdinalIgnoreCase);
			foreach (var old in previousMarkup?.Disputes ?? new List<DisputedComponent>())
			{
				if (old?.Points == null || old.Points.Count == 0 || string.IsNullOrWhiteSpace(old.Id))
					continue;
				if (old.Points.Any(x => freshPointIds.Contains(x.AnchorId)))
					continue;
				foreach (var pt in old.Points)
					pt.Candidates = (pt.Candidates ?? new List<DisputeCandidate>())
						.Where(c => !string.IsNullOrWhiteSpace(c?.AnchorId) && validAnchorIds.Contains(c.AnchorId))
						.ToList();
				var pending = old.Points.Any(pt => pt.Status != DisputeAccepted && pt.HasUserData && pt.Candidates.Count > 0
					&& (pt.IsManual ? validAnchorIds.Contains(pt.AnchorId) : lostAnchorIds.Contains(pt.AnchorId)));
				if (pending)
					result.Add(old);
			}

			return result;
		}

		private static DisputedPoint SnapshotDisputedPoint(TreeNode a) => new DisputedPoint
		{
			AnchorId = a.Id,
			Name = a.Name,
			Filepath = a.Filepath,
			Language = a.Language,
			AnchorKind = a.AnchorKind,
			AnchorFamily = EnsureAnchorFamily(a),
			ParentNameRaw = a.ParentNameRaw,
			MethodNameNorm = a.MethodNameNorm,
			GqlTypeKind = a.GqlTypeKind,
			Comment = a.Comment,
			IsManual = a.IsManual,
		};

		private static string MakeDisputeId(IEnumerable<DisputedPoint> points)
			=> "dispute:" + Sha256Hex(string.Join("|", points.Select(x => x.AnchorId ?? "").OrderBy(x => x, StringComparer.Ordinal))).Substring(0, 16);

		/// <summary>Заголовок компоненты: «User: name, login, nick» (общий родитель, если он один).</summary>
		internal static string DisputeTitle(DisputedComponent d)
		{
			var pts = d?.Points ?? new List<DisputedPoint>();
			var names = pts.Select(x => string.IsNullOrWhiteSpace(x.Name) ? "?" : x.Name).ToList();
			var shown = names.Count <= 4 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" и ещё {names.Count - 3}";
			var members = pts.All(x => string.Equals(x.AnchorKind, AnchorKindGqlField, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(x.AnchorKind, AnchorKindTs, StringComparison.OrdinalIgnoreCase));
			var parents = pts.Select(x => x.ParentNameRaw).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			return (members && parents.Count == 1 ? parents[0] + ": " : "") + shown;
		}

		/// <summary>Системная группа «_Спорные (N)»: по узлу на компоненту (щелчок открывает окно решения).</summary>
		private TreeNode BuildDisputesRoot(PersistedMarkup markup)
		{
			var list = (markup?.Disputes ?? new List<DisputedComponent>())
				.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id) && d.Points != null && d.Points.Count > 0)
				.ToList();
			if (list.Count == 0)
				return null;

			var root = new TreeNode
			{
				Id = "system:disputes",
				Name = $"_Спорные ({list.Count})",
				NodeType = "group",
				SystemKind = "disputes",
				Children = new List<TreeNode>(),
			};
			foreach (var d in list)
				root.Children.Add(new TreeNode
				{
					Id = d.Id,
					Name = DisputeTitle(d),
					NodeType = "group",
					SystemKind = "dispute",
					Children = new List<TreeNode>(),
				});
			return root;
		}

		/// <summary>Id непринятых точек ожидающих компонент — чтобы пометить их в _Lost.</summary>
		private static HashSet<string> PendingDisputedPointIds(PersistedMarkup markup)
			=> new HashSet<string>(
				(markup?.Disputes ?? new List<DisputedComponent>())
					.SelectMany(d => d?.Points ?? new List<DisputedPoint>())
					.Where(pt => pt != null && pt.Status != DisputeAccepted && !string.IsNullOrWhiteSpace(pt.AnchorId))
					.Select(pt => pt.AnchorId),
				StringComparer.OrdinalIgnoreCase);

		// ================================================================ land/getDispute

		[JsonRpcMethod("land/getDispute", UseSingleObjectParameterDeserialization = true)]
		public Task<DisputeViewV2> GetDisputeAsync(GetDisputeParams p)
		{
			lock (markupLock)
			{
				var disputes = currentMarkup?.Disputes ?? new List<DisputedComponent>();
				var ids = disputes.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id)).Select(d => d.Id).ToList();
				if (ids.Count == 0)
					return Task.FromResult(new DisputeViewV2 { ids = ids, m = "Спорных групп нет." });

				var dispute = string.IsNullOrWhiteSpace(p?.disputeId)
					? disputes.First(d => d != null && !string.IsNullOrWhiteSpace(d.Id))
					: disputes.FirstOrDefault(d => string.Equals(d?.Id, p.disputeId, StringComparison.OrdinalIgnoreCase));
				if (dispute == null)
					return Task.FromResult(new DisputeViewV2 { ids = ids, m = "Спорная группа не найдена — возможно, уже решена." });

				var view = BuildDisputeViewUnsafe(dispute, p?.fixedChoices);
				view.ids = ids;
				return Task.FromResult(view);
			}
		}

		private Dictionary<string, TreeNode> LiveAnchorsByIdUnsafe()
		{
			var live = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
			foreach (var a in currentMarkup?.Anchors ?? new List<TreeNode>())
				if (a != null && string.Equals(a.NodeType, "anchor", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.Id))
					live[a.Id] = a;
			return live;
		}

		private DisputeViewV2 BuildDisputeViewUnsafe(DisputedComponent d, IReadOnlyList<DisputeChoiceParam> fixedChoices)
		{
			var view = new DisputeViewV2 { id = d.Id, title = DisputeTitle(d) };
			var live = LiveAnchorsByIdUnsafe();
			var lostById = (currentMarkup?.LostAnchors ?? new List<LostAnchor>())
				.Where(l => !string.IsNullOrWhiteSpace(l?.AnchorId))
				.GroupBy(l => l.AnchorId, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			var pts = d.Points;

			// кандидаты компоненты → индексы для AssignmentProbabilities; только живые якоря
			var candIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var candIds = new List<string>();
			var edges = new List<IReadOnlyList<(int Cand, double Dist)>>();
			foreach (var pt in pts)
			{
				var list = new List<(int Cand, double Dist)>();
				foreach (var c in (pt.Candidates ?? new List<DisputeCandidate>()).OrderBy(c => c.Dist))
				{
					if (string.IsNullOrWhiteSpace(c?.AnchorId) || !live.ContainsKey(c.AnchorId)) continue;
					if (!candIndex.TryGetValue(c.AnchorId, out var ci))
					{
						ci = candIds.Count;
						candIndex[c.AnchorId] = ci;
						candIds.Add(c.AnchorId);
					}
					if (!list.Any(e => e.Cand == ci)) list.Add((ci, c.Dist));
				}
				edges.Add(list);
			}

			// выбор пользователя: Id якоря или «не переносить»
			var pins = new Dictionary<int, int>();
			var pinEcho = new Dictionary<int, string>();
			foreach (var fc in fixedChoices ?? new List<DisputeChoiceParam>())
			{
				var pi = pts.FindIndex(x => string.Equals(x.AnchorId, fc?.point, StringComparison.OrdinalIgnoreCase));
				if (pi < 0) continue;
				if (string.IsNullOrWhiteSpace(fc.target))
				{
					pins[pi] = AssignmentProbabilities.None;
					pinEcho[pi] = "";
				}
				else if (candIndex.TryGetValue(fc.target, out var ci))
				{
					pins[pi] = ci;
					pinEcho[pi] = candIds[ci];
				}
			}

			var rs = RebindSettings.Default;
			var t = rs.Temperature;
			var r = d.RejectCost > 0 ? d.RejectCost : rs.RejectCost;
			var unconditional = AssignmentProbabilities.Component(edges, t, r, null, topCount: 1);
			var conditional = pins.Count == 0 ? AssignmentProbabilities.Component(edges, t, r, null, topCount: 3)
				: AssignmentProbabilities.Component(edges, t, r, pins, topCount: 3);
			if (conditional.Top.Count == 0)
				view.m = "Выбор несовместим: одно поле выбрано для двух точек.";
			var shown = conditional.Top.Count > 0 ? conditional : unconditional;
			view.exact = shown.Exact;
			view.variants = shown.VariantCount;

			string IdOf(int ci) => ci == AssignmentProbabilities.None ? null : candIds[ci];

			for (int i = 0; i < pts.Count; i++)
			{
				var pt = pts[i];
				var comment = live.TryGetValue(pt.AnchorId, out var own) && (pt.Status == DisputeAccepted || pt.IsManual) ? own.Comment
					: (lostById.TryGetValue(pt.AnchorId, out var lost) ? lost.Comment : pt.Comment);
				var dp = new DisputePointV2
				{
					id = pt.AnchorId,
					n = pt.Name,
					f = pt.Filepath,
					k = pt.AnchorKind,
					cm = string.IsNullOrWhiteSpace(comment) ? null : comment,
					man = pt.IsManual ? true : null,
					ud = pt.HasUserData ? true : null,
					st = pt.Status,
					auto = pt.Status == DisputeAccepted && candIndex.ContainsKey(pt.AnchorId) ? pt.AnchorId : null,
				};
				if (unconditional.Top.Count > 0)
				{
					var b = unconditional.Top[0].Choice[i];
					dp.bt = IdOf(b);
					dp.bp = unconditional.RowP(i, b);
				}
				if (pinEcho.TryGetValue(i, out var pe))
					dp.pin = pe;

				// проценты в списке: у строки с выбором пользователя — без её собственного выбора
				var menu = conditional;
				if (pins.ContainsKey(i))
				{
					var others = pins.Where(kv => kv.Key != i).ToDictionary(kv => kv.Key, kv => kv.Value);
					menu = AssignmentProbabilities.Component(edges, t, r, others, topCount: 0);
				}
				foreach (var (ci, dist) in edges[i])
				{
					var node = live[candIds[ci]];
					dp.o.Add(new DisputeOptionV2
					{
						t = candIds[ci],
						n = node.Name,
						f = node.Filepath,
						s = node.StartOffset,
						e = node.EndOffset,
						k = node.AnchorKind,
						d = dist,
						p = menu.RowP(i, ci),
					});
				}
				dp.o.Add(new DisputeOptionV2 { t = null, d = r, p = menu.RowP(i, AssignmentProbabilities.None) });
				view.points.Add(dp);
			}

			foreach (var v in conditional.Top)
				view.top.Add(new DisputeVariantV2 { p = v.P, c = v.Cost, a = v.Choice.Select(IdOf).ToList() });
			return view;
		}

		// ================================================================ land/resolveDispute

		[JsonRpcMethod("land/resolveDispute", UseSingleObjectParameterDeserialization = true)]
		public Task<ResolveDisputeResultV2> ResolveDisputeAsync(ResolveDisputeParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.disputeId))
				throw new ArgumentException("disputeId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			ResolveDisputeResultV2 Fail(string m) => new ResolveDisputeResultV2 { ok = false, m = m, left = currentMarkup?.Disputes?.Count ?? 0 };

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				currentMarkup.Links ??= new List<AnchorLink>();
				currentMarkup.LostAnchors ??= new List<LostAnchor>();
				currentMarkup.Disputes ??= new List<DisputedComponent>();

				var d = currentMarkup.Disputes.FirstOrDefault(x => string.Equals(x?.Id, p.disputeId, StringComparison.OrdinalIgnoreCase));
				if (d == null)
					return Task.FromResult(Fail("Спорная группа не найдена — возможно, уже решена."));

				var live = LiveAnchorsByIdUnsafe();
				var choice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (var c in p.choices ?? new List<DisputeChoiceParam>())
					if (!string.IsNullOrWhiteSpace(c?.point))
						choice[c.point] = string.IsNullOrWhiteSpace(c.target) ? null : c.target;

				var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var pt in d.Points)
				{
					if (!choice.TryGetValue(pt.AnchorId, out var tgt))
						return Task.FromResult(Fail($"Нет выбора для точки «{pt.Name}»."));
					if (tgt == null)
						continue;
					if (!(pt.Candidates ?? new List<DisputeCandidate>()).Any(c => string.Equals(c?.AnchorId, tgt, StringComparison.OrdinalIgnoreCase))
						|| !live.ContainsKey(tgt))
						return Task.FromResult(Fail($"Поле для точки «{pt.Name}» больше не найдено — обновите окно."));
					if (!taken.Add(tgt))
						return Task.FromResult(Fail("Одно поле выбрано для двух точек."));
				}

				int moved = 0, lostCount = 0;
				var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				var lostById = currentMarkup.LostAnchors
					.Where(l => !string.IsNullOrWhiteSpace(l?.AnchorId))
					.GroupBy(l => l.AnchorId, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

				// ---- авто-точки: Id переходят так, как их передала бы сшивка при этой расстановке
				var autoPts = d.Points.Where(x => !x.IsManual).ToList();
				if (autoPts.Count > 0)
				{
					var pointIds = new HashSet<string>(autoPts.Select(x => x.AnchorId), StringComparer.OrdinalIgnoreCase);
					var nodes = autoPts
						.SelectMany(x => x.Candidates ?? new List<DisputeCandidate>())
						.Select(c => c?.AnchorId)
						.Where(id => !string.IsNullOrWhiteSpace(id) && live.ContainsKey(id))
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.ToDictionary(id => id, id => live[id], StringComparer.OrdinalIgnoreCase);

					// «свежий» Id узла: для узла, которому сшивка передала Id принятой точки, — Id до передачи
					var freshIdOf = new Dictionary<TreeNode, string>(ReferenceEqualityComparer.Instance);
					foreach (var kv in nodes)
						freshIdOf[kv.Value] = kv.Key;
					foreach (var pt in autoPts)
						if (pt.Status == DisputeAccepted && nodes.TryGetValue(pt.AnchorId, out var applied))
							freshIdOf[applied] = string.IsNullOrWhiteSpace(pt.AppliedFreshId)
								? NewAnchorId((applied.Language ?? "gql").ToLowerInvariant())
								: pt.AppliedFreshId;

					var desired = new Dictionary<TreeNode, string>(freshIdOf, ReferenceEqualityComparer.Instance);
					var pointOfNode = new Dictionary<TreeNode, DisputedPoint>(ReferenceEqualityComparer.Instance);
					foreach (var pt in autoPts)
						if (choice[pt.AnchorId] is string tgt && nodes.TryGetValue(tgt, out var node))
						{
							desired[node] = pt.AnchorId;
							pointOfNode[node] = pt;
						}

					// комментарий точки — текущий: у применённой точки он на узле, у потерянной — в _Lost
					var pointComment = autoPts.ToDictionary(
						pt => pt.AnchorId,
						pt => live.TryGetValue(pt.AnchorId, out var n) ? n.Comment
							: (lostById.TryGetValue(pt.AnchorId, out var l) ? l.Comment : pt.Comment),
						StringComparer.OrdinalIgnoreCase);

					// ссылки, заведённые после рескана на «свежий» Id узла, который становится точкой, переходят к точке
					foreach (var kv in pointOfNode)
					{
						var cur = kv.Key.Id;
						var pid = kv.Value.AnchorId;
						if (pointIds.Contains(cur) || string.Equals(cur, pid, StringComparison.OrdinalIgnoreCase))
							continue;
						MoveUserReferencesUnsafe(cur, pid);
						if (string.IsNullOrWhiteSpace(pointComment[pid]) && !string.IsNullOrWhiteSpace(kv.Key.Comment))
							pointComment[pid] = kv.Key.Comment;
					}

					// переименование всех узлов разом (перестановки вроде name ↔ login корректны)
					foreach (var kv in desired)
					{
						var node = kv.Key;
						var old = node.Id;
						var neu = kv.Value;
						if (string.Equals(old, neu, StringComparison.Ordinal))
							continue;
						renames[old] = neu;
						node.Id = neu;
						node.Comment = pointOfNode.TryGetValue(node, out var owner) ? pointComment[owner.AnchorId]
							: (pointIds.Contains(old) ? null : node.Comment);
					}

					foreach (var pt in autoPts)
					{
						if (choice[pt.AnchorId] != null)
						{
							moved++;
							currentMarkup.LostAnchors.RemoveAll(l => string.Equals(l?.AnchorId, pt.AnchorId, StringComparison.OrdinalIgnoreCase));
						}
						else if (pt.HasUserData || HasUserReferencesUnsafe(pt.AnchorId) || !string.IsNullOrWhiteSpace(pointComment[pt.AnchorId]))
						{
							EnsureLostAnchorUnsafe(pt, pointComment[pt.AnchorId]);
							lostCount++;
						}
					}
				}

				// ---- ручные точки: данные пользователя переходят на выбранный якорь, сама ручная точка снимается;
				//      «не переносить» — точка остаётся на прежнем месте
				foreach (var pt in d.Points.Where(x => x.IsManual))
				{
					var tgt = choice[pt.AnchorId];
					if (tgt == null || !live.TryGetValue(tgt, out var target))
						continue;
					MoveUserReferencesUnsafe(pt.AnchorId, tgt);
					if (string.IsNullOrWhiteSpace(target.Comment) && live.TryGetValue(pt.AnchorId, out var manualNode)
						&& !string.IsNullOrWhiteSpace(manualNode.Comment))
						target.Comment = manualNode.Comment;
					currentMarkup.Anchors.RemoveAll(a => a != null && string.Equals(a.Id, pt.AnchorId, StringComparison.OrdinalIgnoreCase));
					renames[pt.AnchorId] = tgt;
					moved++;
				}

				currentMarkup.Disputes.RemoveAll(x => string.Equals(x?.Id, d.Id, StringComparison.OrdinalIgnoreCase));
				ApplyRenamesToPendingUnsafe(renames);
				RebuildRelationsUnsafe();
				PersistMarkupUnsafe("resolveDispute");
				Debug($"[resolveDispute] {d.Id}: moved={moved}, lost={lostCount}, renamed={renames.Count}");
				return Task.FromResult(new ResolveDisputeResultV2
				{
					ok = true,
					m = "Спорная группа решена.",
					moved = moved,
					lost = lostCount,
					left = currentMarkup.Disputes.Count,
				});
			}
		}

		/// <summary>Членства и пользовательские связи с Id <paramref name="fromId"/> переходят на <paramref name="toId"/> (без дублей).</summary>
		private void MoveUserReferencesUnsafe(string fromId, string toId)
		{
			if (string.IsNullOrWhiteSpace(fromId) || string.IsNullOrWhiteSpace(toId)
				|| string.Equals(fromId, toId, StringComparison.OrdinalIgnoreCase))
				return;

			var members = currentMarkup.Memberships ?? new List<UserGroupMembership>();
			var kept = new List<UserGroupMembership>();
			foreach (var m in members)
			{
				if (m == null) continue;
				if (!string.Equals(m.AnchorId, fromId, StringComparison.OrdinalIgnoreCase)) { kept.Add(m); continue; }
				var dup = members.Any(x => x != null
					&& string.Equals(x.GroupId, m.GroupId, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(x.AnchorId, toId, StringComparison.OrdinalIgnoreCase));
				if (!dup)
					kept.Add(new UserGroupMembership { GroupId = m.GroupId, AnchorId = toId, Order = m.Order, AddedAtUtc = m.AddedAtUtc });
			}
			currentMarkup.Memberships = kept;

			foreach (var l in currentMarkup.Links ?? new List<AnchorLink>())
			{
				if (l == null || IsAutoPair(l)) continue;
				if (string.Equals(l.SourceAnchorId, fromId, StringComparison.OrdinalIgnoreCase)) l.SourceAnchorId = toId;
				if (string.Equals(l.TargetAnchorId, fromId, StringComparison.OrdinalIgnoreCase)) l.TargetAnchorId = toId;
			}
			currentMarkup.Links = (currentMarkup.Links ?? new List<AnchorLink>())
				.Where(l => l != null && (IsAutoPair(l) || !string.Equals(l.SourceAnchorId, l.TargetAnchorId, StringComparison.OrdinalIgnoreCase)))
				.GroupBy(l => $"{l.SourceAnchorId}|{l.TargetAnchorId}|{l.Kind ?? ""}", StringComparer.OrdinalIgnoreCase)
				.Select(g => g.First())
				.ToList();
		}

		private bool HasUserReferencesUnsafe(string anchorId)
			=> (currentMarkup.Memberships ?? new List<UserGroupMembership>()).Any(m => string.Equals(m?.AnchorId, anchorId, StringComparison.OrdinalIgnoreCase))
				|| (currentMarkup.Links ?? new List<AnchorLink>()).Any(l => l != null && !IsAutoPair(l)
					&& (string.Equals(l.SourceAnchorId, anchorId, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(l.TargetAnchorId, anchorId, StringComparison.OrdinalIgnoreCase)));

		/// <summary>Точка, для которой выбрано «не переносить», попадает в _Lost (если её там ещё нет).</summary>
		private void EnsureLostAnchorUnsafe(DisputedPoint pt, string comment)
		{
			if (currentMarkup.LostAnchors.Any(l => string.Equals(l?.AnchorId, pt.AnchorId, StringComparison.OrdinalIgnoreCase)))
				return;
			var groupNames = (currentMarkup.Memberships ?? new List<UserGroupMembership>())
				.Where(m => string.Equals(m?.AnchorId, pt.AnchorId, StringComparison.OrdinalIgnoreCase))
				.Select(m => (currentMarkup.UserGroups ?? new List<UserGroup>())
					.FirstOrDefault(g => string.Equals(g?.Id, m.GroupId, StringComparison.OrdinalIgnoreCase))?.Name)
				.Where(s => !string.IsNullOrWhiteSpace(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			currentMarkup.LostAnchors.Add(new LostAnchor
			{
				AnchorId = pt.AnchorId,
				Name = pt.Name,
				Filepath = pt.Filepath,
				Language = pt.Language,
				AnchorKind = pt.AnchorKind,
				AnchorFamily = pt.AnchorFamily,
				ParentNameRaw = pt.ParentNameRaw,
				MethodNameNorm = pt.MethodNameNorm,
				GqlTypeKind = pt.GqlTypeKind,
				LostAtUtc = DateTime.UtcNow,
				WasInGroupNames = groupNames,
				Comment = comment,
				Candidates = (pt.Candidates ?? new List<DisputeCandidate>()).Select(c => new DisputeCandidate { AnchorId = c.AnchorId, Dist = c.Dist }).ToList(),
			});
		}

		/// <summary>Кандидаты в других спорных компонентах и в _Lost ссылаются на якоря по Id: переименования применяются и к ним.</summary>
		private void ApplyRenamesToPendingUnsafe(Dictionary<string, string> renames)
		{
			if (renames == null || renames.Count == 0)
				return;
			void Rename(List<DisputeCandidate> cands)
			{
				foreach (var c in cands ?? new List<DisputeCandidate>())
					if (c != null && !string.IsNullOrWhiteSpace(c.AnchorId) && renames.TryGetValue(c.AnchorId, out var neu))
						c.AnchorId = neu;
			}
			foreach (var d in currentMarkup.Disputes ?? new List<DisputedComponent>())
				foreach (var pt in d?.Points ?? new List<DisputedPoint>())
					Rename(pt.Candidates);
			foreach (var l in currentMarkup.LostAnchors ?? new List<LostAnchor>())
				Rename(l?.Candidates);
		}

		/// <summary>Точка снята со спорной компоненты вне окна (recover/discard/delete): компонента снимается целиком.</summary>
		private void DropDisputesWithPointUnsafe(string anchorId)
		{
			if (string.IsNullOrWhiteSpace(anchorId) || currentMarkup?.Disputes == null)
				return;
			currentMarkup.Disputes.RemoveAll(d => d?.Points != null
				&& d.Points.Any(pt => string.Equals(pt?.AnchorId, anchorId, StringComparison.OrdinalIgnoreCase)));
		}

		// ================================================================ кандидаты одиночной точки

		/// <summary>Проценты кандидатов одной точки: softmax по расстояниям и «не переносить» ценой R.</summary>
		private List<RebindCandidateV2> MakeRebindCandidates(IReadOnlyList<(TreeNode Node, double Dist)> cands, double rejectCost, out double pNone)
		{
			var rs = RebindSettings.Default;
			var probs = AssignmentProbabilities.Point(cands.Select(c => c.Dist).ToList(), rs.Temperature, rejectCost);
			pNone = probs[probs.Length - 1];
			return cands.Select((c, i) => new RebindCandidateV2 { n = ToClientNodeV2(c.Node), d = c.Dist, p = probs[i] })
				.OrderByDescending(c => c.p)
				.ToList();
		}

		[JsonRpcMethod("land/getLostCandidates", UseSingleObjectParameterDeserialization = true)]
		public Task<RebindCandidatesResultV2> GetLostCandidatesAsync(LostCandidatesParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.lostAnchorId))
				throw new ArgumentException("lostAnchorId is required");

			lock (markupLock)
			{
				var lost = (currentMarkup?.LostAnchors ?? new List<LostAnchor>())
					.FirstOrDefault(l => string.Equals(l?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase));
				if (lost == null)
					return Task.FromResult(new RebindCandidatesResultV2 { m = "Утерянный якорь не найден." });

				var live = LiveAnchorsByIdUnsafe();
				var cands = (lost.Candidates ?? new List<DisputeCandidate>())
					.Where(c => !string.IsNullOrWhiteSpace(c?.AnchorId) && live.ContainsKey(c.AnchorId))
					.OrderBy(c => c.Dist)
					.Select(c => (live[c.AnchorId], c.Dist))
					.ToList();
				if (cands.Count == 0)
					return Task.FromResult(new RebindCandidatesResultV2 { m = "Сохранённых кандидатов нет." });

				// цена «не переносить» — как у шага назначения, с порогом профиля точки
				var stitch = StitchSettings.Default;
				var tau = stitch.GetThreshold(GetProfileKey(lost.Language, lost.AnchorFamily, lost.AnchorKind));
				var r = double.IsNaN(stitch.RejectCost) ? tau / (1.0 - stitch.MinRelativeMargin) : stitch.RejectCost;
				var list = MakeRebindCandidates(cands, r > 0 ? r : RebindSettings.Default.RejectCost, out var pNone);
				return Task.FromResult(new RebindCandidatesResultV2 { cs = list, pz = pNone });
			}
		}
	}
}
