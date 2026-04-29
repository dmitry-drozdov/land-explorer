using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		// =========================================================================
		// Cross-anchor links (этап 2): пользовательские направленные связи между
		// якорями. Связь хранится один раз (source -> target), но в UI отображается
		// с обеих сторон (Outgoing / Incoming).
		//
		// AnchorId стабилен через rebind (manual) и через сшивку Id (auto, этап 0),
		// поэтому Link не "уезжает" при перепривязке. Если auto-якорь утерян при
		// сшивке — Link перевешивается в LostAnchor (см. ListAnchors).
		// =========================================================================

		private const string LinkIdPrefix = "link:";
		// Системный kind для auto-pair связей (gqlField ↔ ts-резолвер).
		// Эти связи синтезируются из Relations и:
		//  - НЕ сохраняются на диск (фильтруются в AnchorStore.TrySave);
		//  - НЕ учитываются в счётчиках oc/ic у бейджа;
		//  - НЕ показываются в listLinks (Outgoing/Incoming);
		//  - НЕ создаются через addLink (kind зарезервирован).
		// Префикс "_" — соглашение «системный, не для user-UI».
		internal const string AutoPairKind = "_autoPair";

		private static bool IsAutoPair(AnchorLink l)
			=> l != null && string.Equals(l.Kind, AutoPairKind, StringComparison.Ordinal);

		private static LinkClientV2 ToClientLink(AnchorLink link, TreeNode peer, bool peerIsLost)
		{
			if (link == null)
				return null;

			return new LinkClientV2
			{
				id = link.Id,
				src = link.SourceAnchorId,
				tgt = link.TargetAnchorId,
				kind = string.IsNullOrWhiteSpace(link.Kind) ? null : link.Kind,
				label = string.IsNullOrWhiteSpace(link.Label) ? null : link.Label,
				pn = peer?.Name,
				pf = peer?.Filepath,
				ps = peer?.StartOffset,
				pk = peer?.AnchorKind,
				lost = peerIsLost ? true : (bool?)null,
			};
		}

		private static LinkSimpleResultV2 MakeLinkSimpleResult(bool ok, string message = null)
			=> new LinkSimpleResultV2 { ok = ok, m = message };

		[JsonRpcMethod("land/addLink", UseSingleObjectParameterDeserialization = true)]
		public Task<LinkResultV2> AddLinkAsync(AddLinkParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.sourceId) || string.IsNullOrWhiteSpace(p.targetId))
				throw new ArgumentException("sourceId and targetId are required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			if (string.Equals(p.sourceId, p.targetId, StringComparison.OrdinalIgnoreCase))
				return Task.FromResult(new LinkResultV2 { l = null, m = "Якорь не может быть связан сам с собой." });

			// Системный kind зарезервирован под auto-pair (gqlField↔ts-резолвер),
			// синтезируется автоматически на основе Relations.
			if (string.Equals(p.kind, AutoPairKind, StringComparison.Ordinal))
				return Task.FromResult(new LinkResultV2 { l = null, m = "Этот тип связи зарезервирован системой." });

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				currentMarkup.Links ??= new List<AnchorLink>();

				var anchorIds = new HashSet<string>(
					currentMarkup.Anchors
						.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id))
						.Select(a => a.Id),
					StringComparer.OrdinalIgnoreCase);

				if (!anchorIds.Contains(p.sourceId))
					return Task.FromResult(new LinkResultV2 { l = null, m = "Source-якорь не найден в текущем снимке." });
				if (!anchorIds.Contains(p.targetId))
					return Task.FromResult(new LinkResultV2 { l = null, m = "Target-якорь не найден в текущем снимке." });

				// Дедуп: ту же пару (source, target) с тем же kind не дублируем.
				var existing = currentMarkup.Links.FirstOrDefault(l =>
					l != null
					&& string.Equals(l.SourceAnchorId, p.sourceId, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(l.TargetAnchorId, p.targetId, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(l.Kind ?? "", p.kind ?? "", StringComparison.OrdinalIgnoreCase));

				if (existing != null)
				{
					Debug($"[addLink] duplicate ignored: {p.sourceId} -> {p.targetId} (kind={p.kind})");
					var peer = nodesById.TryGetValue(existing.TargetAnchorId, out var t) ? t : null;
					return Task.FromResult(new LinkResultV2 { l = ToClientLink(existing, peer, false), m = "Такая связь уже существует." });
				}

				var link = new AnchorLink
				{
					Id = LinkIdPrefix + Guid.NewGuid().ToString("N"),
					SourceAnchorId = p.sourceId,
					TargetAnchorId = p.targetId,
					Kind = string.IsNullOrWhiteSpace(p.kind) ? null : p.kind.Trim(),
					Label = string.IsNullOrWhiteSpace(p.label) ? null : p.label.Trim(),
					CreatedAtUtc = DateTime.UtcNow,
				};
				currentMarkup.Links.Add(link);

				PersistMarkupUnsafe("addLink");
				Debug($"[addLink] {link.Id}: {p.sourceId} -> {p.targetId} (kind={link.Kind}, label={link.Label})");

				var peerNode = nodesById.TryGetValue(link.TargetAnchorId, out var peerN) ? peerN : null;
				return Task.FromResult(new LinkResultV2 { l = ToClientLink(link, peerNode, false), m = "Связь создана." });
			}
		}

		[JsonRpcMethod("land/removeLink", UseSingleObjectParameterDeserialization = true)]
		public Task<LinkSimpleResultV2> RemoveLinkAsync(RemoveLinkParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.linkId))
				throw new ArgumentException("linkId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				currentMarkup.Links ??= new List<AnchorLink>();

				var before = currentMarkup.Links.Count;
				currentMarkup.Links = currentMarkup.Links
					.Where(l => !string.Equals(l?.Id, p.linkId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var removed = before - currentMarkup.Links.Count;

				if (removed == 0)
					return Task.FromResult(MakeLinkSimpleResult(false, "Связь не найдена."));

				PersistMarkupUnsafe("removeLink");
				Debug($"[removeLink] removed {p.linkId}");
				return Task.FromResult(MakeLinkSimpleResult(true, "Связь удалена."));
			}
		}

		[JsonRpcMethod("land/listLinks", UseSingleObjectParameterDeserialization = true)]
		public Task<LinksListResultV2> ListLinksAsync(ListLinksParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				var links = currentMarkup.Links ?? new List<AnchorLink>();
				var lostIds = new HashSet<string>(
					(currentMarkup.LostAnchors ?? new List<LostAnchor>())
						.Where(l => l != null && !string.IsNullOrWhiteSpace(l.AnchorId))
						.Select(l => l.AnchorId),
					StringComparer.OrdinalIgnoreCase);

				var lostByAnchorId = (currentMarkup.LostAnchors ?? new List<LostAnchor>())
					.Where(l => l != null && !string.IsNullOrWhiteSpace(l.AnchorId))
					.GroupBy(l => l.AnchorId, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

				TreeNode ResolvePeer(string anchorId, out bool isLost)
				{
					isLost = false;
					if (string.IsNullOrWhiteSpace(anchorId))
						return null;

					if (nodesById.TryGetValue(anchorId, out var live) && live != null)
						return live;

					if (lostByAnchorId.TryGetValue(anchorId, out var lost))
					{
						isLost = true;
						return new TreeNode
						{
							Id = lost.AnchorId,
							Name = lost.Name,
							NodeType = "anchor",
							Filepath = lost.Filepath,
							Language = lost.Language,
							AnchorKind = lost.AnchorKind,
							ParentNameRaw = lost.ParentNameRaw,
							MethodNameNorm = lost.MethodNameNorm,
							GqlTypeKind = lost.GqlTypeKind,
						};
					}

					return null;
				}

				var outgoing = new List<LinkClientV2>();
				var incoming = new List<LinkClientV2>();

				// Системные auto-pair (gqlField↔ts) фильтруем — пользователь видит их
				// только через префикс ◆, не через QuickPick связей.
				foreach (var l in links.Where(x => x != null && !IsAutoPair(x)))
				{
					var isOutgoing = string.Equals(l.SourceAnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase);
					var isIncoming = string.Equals(l.TargetAnchorId, p.anchorId, StringComparison.OrdinalIgnoreCase);
					if (!isOutgoing && !isIncoming)
						continue;

					var peerId = isOutgoing ? l.TargetAnchorId : l.SourceAnchorId;
					var peer = ResolvePeer(peerId, out var peerLost);

					var dto = ToClientLink(l, peer, peerLost);
					if (isOutgoing)
						outgoing.Add(dto);
					else
						incoming.Add(dto);
				}

				return Task.FromResult(new LinksListResultV2
				{
					outgoing = outgoing.OrderBy(x => x.pn ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
					incoming = incoming.OrderBy(x => x.pn ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
				});
			}
		}

		// Каскад при удалении anchor: вызывается из DeleteAnchorAsync под markupLock.
		// Возвращает количество удалённых связей.
		private int RemoveLinksForAnchorUnsafe(string anchorId)
		{
			if (string.IsNullOrWhiteSpace(anchorId))
				return 0;

			currentMarkup ??= new PersistedMarkup();
			currentMarkup.Links ??= new List<AnchorLink>();

			var before = currentMarkup.Links.Count;
			currentMarkup.Links = currentMarkup.Links
				.Where(l => l != null
					&& !string.Equals(l.SourceAnchorId, anchorId, StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(l.TargetAnchorId, anchorId, StringComparison.OrdinalIgnoreCase))
				.ToList();
			return before - currentMarkup.Links.Count;
		}

		// Подсчёт outgoing/incoming для конкретного якоря — нужно для бейджей в дереве.
		// Вызывается из ToClientNodeV2 при наличии currentMarkup.Links; должен быть быстрым.
		// Кэшируется в RebuildMarkupRootsUnsafe через _linkCountByAnchor.
		// Параллельно собирается _pairedAnchorIds — set anchor-Id, у которых есть auto-pair
		// (gqlField↔ts) — для проставления pr: true в DTO (префикс ◆ на клиенте).
		private Dictionary<string, (int outCount, int inCount)> _linkCountByAnchor = new(StringComparer.OrdinalIgnoreCase);
		private HashSet<string> _pairedAnchorIds = new(StringComparer.OrdinalIgnoreCase);

		private void RebuildLinkCountIndexUnsafe()
		{
			_linkCountByAnchor = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
			_pairedAnchorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var links = currentMarkup?.Links;
			if (links == null || links.Count == 0)
				return;

			foreach (var l in links)
			{
				if (l == null) continue;

				// Auto-pair (системные) идут в отдельный set — они не считаются в счётчиках,
				// но дают маркер ◆ обоим эндпоинтам.
				if (IsAutoPair(l))
				{
					if (!string.IsNullOrWhiteSpace(l.SourceAnchorId))
						_pairedAnchorIds.Add(l.SourceAnchorId);
					if (!string.IsNullOrWhiteSpace(l.TargetAnchorId))
						_pairedAnchorIds.Add(l.TargetAnchorId);
					continue;
				}

				if (!string.IsNullOrWhiteSpace(l.SourceAnchorId))
				{
					_linkCountByAnchor.TryGetValue(l.SourceAnchorId, out var cur);
					_linkCountByAnchor[l.SourceAnchorId] = (cur.Item1 + 1, cur.Item2);
				}
				if (!string.IsNullOrWhiteSpace(l.TargetAnchorId))
				{
					_linkCountByAnchor.TryGetValue(l.TargetAnchorId, out var cur);
					_linkCountByAnchor[l.TargetAnchorId] = (cur.Item1, cur.Item2 + 1);
				}
			}
		}

		/// <summary>
		/// Удаляет существующие auto-pair Links и пересинтезирует их из текущих Relations.
		/// Вызывается перед RebuildLinkCountIndexUnsafe (внутри RebuildMarkupRootsUnsafe).
		/// Идемпотентен — повторные вызовы при тех же Relations дают тот же результат.
		/// На диск эти связи не уходят (фильтруются в AnchorStore.TrySave).
		/// </summary>
		private void SynthesizeAutoPairLinksUnsafe()
		{
			currentMarkup ??= new PersistedMarkup();
			currentMarkup.Links ??= new List<AnchorLink>();

			// 1. Стираем все существующие auto-pair (если оставались с прошлого раза).
			currentMarkup.Links = currentMarkup.Links
				.Where(l => l == null || !IsAutoPair(l))
				.ToList();

			// 2. Синтезируем заново из Relations (gqlField → tsTargets).
			//    Id auto-pair детерминирован по парам, чтобы при многократных синтезах
			//    те же пары имели те же link.Id (полезно для отладки).
			foreach (var rel in currentMarkup.Relations ?? new List<PersistedRelation>())
			{
				if (string.IsNullOrWhiteSpace(rel?.SourceAnchorId)) continue;
				foreach (var tgt in rel.TargetAnchorIds ?? new List<string>())
				{
					if (string.IsNullOrWhiteSpace(tgt)) continue;
					currentMarkup.Links.Add(new AnchorLink
					{
						Id = $"autoPair:{rel.SourceAnchorId}->{tgt}",
						SourceAnchorId = rel.SourceAnchorId,
						TargetAnchorId = tgt,
						Kind = AutoPairKind,
						CreatedAtUtc = DateTime.UtcNow,
					});
				}
			}
		}

		// =========================================================================
		// Recovery RPC: вызывается из UI на узлах внутри _Lost-bucket-а.
		// =========================================================================

		[JsonRpcMethod("land/recoverLostAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<LostAnchorRecoveryResultV2> RecoverLostAnchorAsync(RecoverLostAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.lostAnchorId) || string.IsNullOrWhiteSpace(p.newAnchorId))
				throw new ArgumentException("lostAnchorId and newAnchorId are required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			if (string.Equals(p.lostAnchorId, p.newAnchorId, StringComparison.OrdinalIgnoreCase))
				return Task.FromResult(new LostAnchorRecoveryResultV2 { ok = false, m = "newAnchorId совпадает с lostAnchorId." });

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				currentMarkup.LostAnchors ??= new List<LostAnchor>();
				currentMarkup.Links ??= new List<AnchorLink>();
				currentMarkup.Memberships ??= new List<UserGroupMembership>();

				var lost = currentMarkup.LostAnchors
					.FirstOrDefault(l => string.Equals(l?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase));
				if (lost == null)
					return Task.FromResult(new LostAnchorRecoveryResultV2 { ok = false, m = "Утерянный якорь не найден." });

				var newAnchorExists = currentMarkup.Anchors
					.Any(a => a != null && string.Equals(a.Id, p.newAnchorId, StringComparison.OrdinalIgnoreCase));
				if (!newAnchorExists)
					return Task.FromResult(new LostAnchorRecoveryResultV2 { ok = false, m = "Целевой якорь не найден в текущем снимке." });

				int recoveredMembers = 0;
				int recoveredLinks = 0;

				// Перевешиваем memberships с lostAnchorId на newAnchorId
				// (только если в той же группе ещё нет канонического membership на newAnchorId).
				var newMembers = new List<UserGroupMembership>();
				foreach (var m in currentMarkup.Memberships)
				{
					if (m == null) { newMembers.Add(m); continue; }
					if (!string.Equals(m.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					{
						newMembers.Add(m);
						continue;
					}

					var alreadyHasNew = currentMarkup.Memberships.Any(x =>
						x != null
						&& string.Equals(x.GroupId, m.GroupId, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(x.AnchorId, p.newAnchorId, StringComparison.OrdinalIgnoreCase));
					if (alreadyHasNew)
					{
						// дублирующая membership — просто отбрасываем lost-membership
						continue;
					}

					newMembers.Add(new UserGroupMembership
					{
						GroupId = m.GroupId,
						AnchorId = p.newAnchorId,
						Order = m.Order,
						AddedAtUtc = m.AddedAtUtc,
					});
					recoveredMembers++;
				}
				currentMarkup.Memberships = newMembers;

				// Перевешиваем links: lostAnchorId меняем на newAnchorId в обоих концах.
				foreach (var link in currentMarkup.Links)
				{
					if (link == null) continue;
					var changed = false;
					if (string.Equals(link.SourceAnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					{
						link.SourceAnchorId = p.newAnchorId;
						changed = true;
					}
					if (string.Equals(link.TargetAnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					{
						link.TargetAnchorId = p.newAnchorId;
						changed = true;
					}
					if (changed) recoveredLinks++;
				}

				// Дедуп после reassign-а: одинаковые (src, tgt, kind) пары удаляем.
				currentMarkup.Links = currentMarkup.Links
					.Where(l => l != null && !string.Equals(l.SourceAnchorId, l.TargetAnchorId, StringComparison.OrdinalIgnoreCase))
					.GroupBy(l => $"{l.SourceAnchorId}|{l.TargetAnchorId}|{l.Kind ?? ""}", StringComparer.OrdinalIgnoreCase)
					.Select(g => g.First())
					.ToList();

				// Удаляем сам LostAnchor.
				currentMarkup.LostAnchors = currentMarkup.LostAnchors
					.Where(l => !string.Equals(l?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					.ToList();

				PersistMarkupUnsafe("recoverLostAnchor");
				Debug($"[recoverLostAnchor] {p.lostAnchorId} -> {p.newAnchorId} (members={recoveredMembers}, links={recoveredLinks})");
				return Task.FromResult(new LostAnchorRecoveryResultV2
				{
					ok = true,
					m = "Утерянный якорь восстановлен.",
					recoveredMemberships = recoveredMembers,
					recoveredLinks = recoveredLinks,
				});
			}
		}

		[JsonRpcMethod("land/discardLostAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<LostAnchorRecoveryResultV2> DiscardLostAnchorAsync(DiscardLostAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.lostAnchorId))
				throw new ArgumentException("lostAnchorId is required");
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			lock (markupLock)
			{
				EnsureMarkupInitializedUnsafe();
				currentMarkup.LostAnchors ??= new List<LostAnchor>();
				currentMarkup.Links ??= new List<AnchorLink>();
				currentMarkup.Memberships ??= new List<UserGroupMembership>();

				var existed = currentMarkup.LostAnchors
					.Any(l => string.Equals(l?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase));
				if (!existed)
					return Task.FromResult(new LostAnchorRecoveryResultV2 { ok = false, m = "Утерянный якорь не найден." });

				var beforeMembers = currentMarkup.Memberships.Count;
				currentMarkup.Memberships = currentMarkup.Memberships
					.Where(m => !string.Equals(m?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var droppedMembers = beforeMembers - currentMarkup.Memberships.Count;

				var beforeLinks = currentMarkup.Links.Count;
				currentMarkup.Links = currentMarkup.Links
					.Where(l => l != null
						&& !string.Equals(l.SourceAnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(l.TargetAnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var droppedLinks = beforeLinks - currentMarkup.Links.Count;

				currentMarkup.LostAnchors = currentMarkup.LostAnchors
					.Where(l => !string.Equals(l?.AnchorId, p.lostAnchorId, StringComparison.OrdinalIgnoreCase))
					.ToList();

				PersistMarkupUnsafe("discardLostAnchor");
				Debug($"[discardLostAnchor] {p.lostAnchorId} (droppedMembers={droppedMembers}, droppedLinks={droppedLinks})");
				return Task.FromResult(new LostAnchorRecoveryResultV2
				{
					ok = true,
					m = "Утерянный якорь отброшен.",
					recoveredMemberships = -droppedMembers,
					recoveredLinks = -droppedLinks,
				});
			}
		}
	}
}
