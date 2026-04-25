using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
	public partial class LandService
	{
		[JsonRpcMethod("land/listAnchors", UseSingleObjectParameterDeserialization = true)]
		public Task<ListTreeResultV2> ListAnchorsAsync(ListTreeParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.folderPath))
				throw new ArgumentException("folderPath is required");

			currentFolderPath = Path.GetFullPath(p.folderPath);

			if (p.preferCache && !p.forceRescan)
			{
				if (AnchorStore.TryLoad(currentFolderPath, out var cached, out var err) && cached != null)
				{
					var hasTypeAnchors = (cached.Anchors ?? new List<TreeNode>()).Any(x => x != null && IsGqlTypeContainerKind(x.AnchorKind));
					if (hasTypeAnchors)
					{
						lock (markupLock)
						{
							currentMarkup = cached;
							RebuildMarkupRootsUnsafe();
							ReloadNodesByIdUnsafe();
						}

						Debug($"[listAnchors] loaded persisted markup from {AnchorStore.GetStorePath(currentFolderPath)} (anchors={nodesById.Count})");
						return Task.FromResult(MakeListTreeResult(currentMarkup.Roots, true));
					}

					Debug("[listAnchors] cache loaded but has no type_def anchors, rescanning project.");
				}
				if (!string.IsNullOrWhiteSpace(err))
					Debug($"[listAnchors] cache load failed: {err}");
			}

			AnchorStore.TryLoad(currentFolderPath, out var previousMarkup, out var previousErr);
			var suppressed = GetSuppressedKeysSet(previousMarkup);

			BuildSemanticMarkupFromDisk(out var gqlAnchors, out var tsAnchors);

			// Сшивка Id у auto-якорей с предыдущим snapshot-ом, чтобы Id переживал
			// рескан при отсутствии семантических изменений. Это нужно для будущих
			// user-групп / пользовательских ссылок, привязанных к стабильному AnchorId.
			// Аварийное отключение: переменная окружения LAND_STITCH_DISABLE=1.
			var lostAutoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.Equals(Environment.GetEnvironmentVariable("LAND_STITCH_DISABLE"), "1", StringComparison.Ordinal))
			{
				var prevAuto = (previousMarkup?.Anchors ?? new List<TreeNode>())
					.Where(x => x != null
						&& string.Equals(x.NodeType, "anchor", StringComparison.OrdinalIgnoreCase)
						&& !x.IsManual)
					.ToList();
				var freshAutoForStitch = gqlAnchors.Concat(tsAnchors).ToList();
				var stitchResult = StitchAutoAnchorIdsFromPrevious(prevAuto, freshAutoForStitch);

				// nodesById был заполнен в BuildSemanticMarkupFromDisk по СТАРЫМ Id
				// тех же node-объектов; после сшивки Id поменялись на месте,
				// поэтому переиндексируем.
				nodesById.Clear();
				foreach (var n in freshAutoForStitch)
				{
					if (!string.IsNullOrWhiteSpace(n?.Id))
						nodesById[n.Id] = n;
				}

				foreach (var lostId in stitchResult.Lost)
				{
					if (!string.IsNullOrWhiteSpace(lostId))
						lostAutoIds.Add(lostId);
				}

				Debug($"[stitch] exact={stitchResult.MatchedExact.Count}, fuzzy={stitchResult.MatchedFuzzy.Count}, lost={stitchResult.Lost.Count}, fresh={stitchResult.Fresh.Count} (prevAuto={prevAuto.Count}, freshAuto={freshAutoForStitch.Count})");
				if (stitchResult.MatchedFuzzy.Count > 0)
				{
					foreach (var (prevId, replacedId, dist) in stitchResult.MatchedFuzzy)
						Debug($"[stitch] fuzzy {prevId} <- (was {replacedId}) dist={dist:F4}");
				}
			}

			var autoAnchors = gqlAnchors.Select(CloneAnchor).Concat(tsAnchors.Select(CloneAnchor))
				.Where(x => !IsSuppressed(x, suppressed))
				.ToList();

			var preservedManualAnchors = new List<TreeNode>();
			var previousManualAnchors = (previousMarkup?.Anchors ?? new List<TreeNode>())
				.Where(x => x != null && x.IsManual)
				.Select(CloneAnchor)
				.Where(x => !IsSuppressed(x, suppressed))
				.ToList();

			if (previousManualAnchors.Count > 0)
			{
				LoadRebindingForestsFromAnchors(autoAnchors);
				foreach (var manual in previousManualAnchors)
				{
					var rebound = RebindAnchorAgainstCurrentForests(manual) ?? CloneAnchor(manual);
					rebound.IsManual = true;
					if (IsSuppressed(rebound, suppressed))
						continue;

					var duplicate = autoAnchors.Concat(preservedManualAnchors)
						.FirstOrDefault(x =>
							x != null
							&& string.Equals(x.Language, rebound.Language, StringComparison.OrdinalIgnoreCase)
							&& string.Equals(x.AnchorKind, rebound.AnchorKind, StringComparison.OrdinalIgnoreCase)
							&& string.Equals(Path.GetFullPath(x.Filepath ?? ""), Path.GetFullPath(rebound.Filepath ?? ""), StringComparison.OrdinalIgnoreCase)
							&& (x.StartOffset ?? -1) == (rebound.StartOffset ?? -2)
							&& (x.EndOffset ?? -1) == (rebound.EndOffset ?? -2));

					if (duplicate == null)
						preservedManualAnchors.Add(rebound);
				}
			}

			var allAnchors = autoAnchors.Concat(preservedManualAnchors).ToList();
			var relations = BuildRelations(
				allAnchors.Where(x => x != null && string.Equals(x.Language, LangGql, StringComparison.OrdinalIgnoreCase)).ToList(),
				allAnchors.Where(x => x != null && string.Equals(x.Language, LangTs, StringComparison.OrdinalIgnoreCase)).ToList());
			Debug($"gqlAnchors={gqlAnchors.Count}, tsAnchors={tsAnchors.Count}, manualAnchors={preservedManualAnchors.Count}, relations={relations.Count}");

			// Переносим user-группы и memberships из previousMarkup.
			// Memberships фильтруем по anchorId: оставляем те, чей якорь либо живой,
			// либо ушёл в _Lost-bucket. Висячие auto-group-Id оставляем —
			// рендер молча проигнорирует их, и они "оживут" если auto-группа вернётся.
			var carriedUserGroups = (previousMarkup?.UserGroups ?? new List<UserGroup>())
				.Where(g => g != null && !string.IsNullOrWhiteSpace(g.Id))
				.ToList();
			var validAnchorIds = new HashSet<string>(allAnchors.Where(a => !string.IsNullOrWhiteSpace(a?.Id)).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

			// _Lost-bucket: для каждого lost-якоря, у которого были memberships или
			// links, создаём LostAnchor snapshot (имя/файл/профиль), чтобы UI
			// мог показать его в системной группе и пользователь смог recover-ить.
			// Если у lost-якоря не было ни memberships, ни links — он просто исчезает.
			var prevMembershipsForLost = previousMarkup?.Memberships ?? new List<UserGroupMembership>();
			var prevLinksForLost = previousMarkup?.Links ?? new List<AnchorLink>();
			var prevAnchorsById = (previousMarkup?.Anchors ?? new List<TreeNode>())
				.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id))
				.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			var prevGroupNamesById = (previousMarkup?.UserGroups ?? new List<UserGroup>())
				.Where(g => g != null && !string.IsNullOrWhiteSpace(g.Id))
				.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.OrdinalIgnoreCase);

			// Имена auto-групп из предыдущего snapshot — для "(was in: ...)" в Lost UI.
			// Auto-группы не персистятся в UserGroups, но мы можем достать их из Roots.
			var prevAutoGroupNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var node in AnchorStore.EnumerateNodes(previousMarkup?.Roots ?? new List<TreeNode>()))
			{
				if (node?.NodeType == "group" && !string.IsNullOrWhiteSpace(node.Id) && !string.IsNullOrWhiteSpace(node.Name)
					&& !prevAutoGroupNamesById.ContainsKey(node.Id))
				{
					prevAutoGroupNamesById[node.Id] = node.Name;
				}
			}

			var newLostList = new List<LostAnchor>();
			foreach (var lostId in lostAutoIds)
			{
				var hasMemberships = prevMembershipsForLost.Any(m => string.Equals(m?.AnchorId, lostId, StringComparison.OrdinalIgnoreCase));
				var hasLinks = prevLinksForLost.Any(l => l != null && (
					string.Equals(l.SourceAnchorId, lostId, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(l.TargetAnchorId, lostId, StringComparison.OrdinalIgnoreCase)));

				if (!hasMemberships && !hasLinks)
					continue;

				if (!prevAnchorsById.TryGetValue(lostId, out var prevAnchor))
					continue;

				var groupNames = prevMembershipsForLost
					.Where(m => string.Equals(m?.AnchorId, lostId, StringComparison.OrdinalIgnoreCase))
					.Select(m => prevGroupNamesById.TryGetValue(m.GroupId ?? "", out var nm)
						? nm
						: (prevAutoGroupNamesById.TryGetValue(m.GroupId ?? "", out var an) ? an : null))
					.Where(s => !string.IsNullOrWhiteSpace(s))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				newLostList.Add(new LostAnchor
				{
					AnchorId = prevAnchor.Id,
					Name = prevAnchor.Name,
					Filepath = prevAnchor.Filepath,
					Language = prevAnchor.Language,
					AnchorKind = prevAnchor.AnchorKind,
					AnchorFamily = prevAnchor.AnchorFamily,
					ParentNameRaw = prevAnchor.ParentNameRaw,
					MethodNameNorm = prevAnchor.MethodNameNorm,
					GqlTypeKind = prevAnchor.GqlTypeKind,
					LostAtUtc = DateTime.UtcNow,
					WasInGroupNames = groupNames,
				});
			}

			// Объединяем с уже существующими LostAnchors из прошлого snapshot, дедуп по AnchorId.
			// Если новый lost совпадает по Id — побеждает новый (свежее метаданные).
			var newLostIds = new HashSet<string>(newLostList.Select(l => l.AnchorId), StringComparer.OrdinalIgnoreCase);
			var carriedLost = new List<LostAnchor>(newLostList);
			foreach (var prevLost in previousMarkup?.LostAnchors ?? new List<LostAnchor>())
			{
				if (prevLost == null || string.IsNullOrWhiteSpace(prevLost.AnchorId)) continue;
				if (newLostIds.Contains(prevLost.AnchorId)) continue;
				// Если живой anchor с тем же Id появился (восстановление через сшивку Id) — тогда не несём.
				if (validAnchorIds.Contains(prevLost.AnchorId)) continue;
				carriedLost.Add(prevLost);
			}
			var lostAnchorIdSet = new HashSet<string>(
				carriedLost.Where(l => !string.IsNullOrWhiteSpace(l?.AnchorId)).Select(l => l.AnchorId),
				StringComparer.OrdinalIgnoreCase);

			var previousMemberships = previousMarkup?.Memberships ?? new List<UserGroupMembership>();
			var carriedMemberships = previousMemberships
				.Where(m => m != null
					&& !string.IsNullOrWhiteSpace(m.GroupId)
					&& !string.IsNullOrWhiteSpace(m.AnchorId)
					&& (validAnchorIds.Contains(m.AnchorId) || lostAnchorIdSet.Contains(m.AnchorId)))
				.ToList();
			var droppedMemberships = previousMemberships.Count - carriedMemberships.Count;
			if (droppedMemberships > 0)
				Debug($"[listAnchors] dropped {droppedMemberships} memberships whose anchor disappeared without a trace");

			// Links: оставляем только если обе стороны (source/target) существуют —
			// либо в живых якорях, либо в _Lost-bucket-е.
			var previousLinks = previousMarkup?.Links ?? new List<AnchorLink>();
			bool LinkSideAlive(string anchorId)
				=> !string.IsNullOrWhiteSpace(anchorId)
					&& (validAnchorIds.Contains(anchorId) || lostAnchorIdSet.Contains(anchorId));
			var carriedLinks = previousLinks
				.Where(l => l != null && LinkSideAlive(l.SourceAnchorId) && LinkSideAlive(l.TargetAnchorId))
				.ToList();
			var droppedLinks = previousLinks.Count - carriedLinks.Count;
			if (droppedLinks > 0)
				Debug($"[listAnchors] dropped {droppedLinks} links whose endpoints disappeared without a trace");

			if (newLostList.Count > 0)
				Debug($"[listAnchors] moved {newLostList.Count} anchors to _Lost (with memberships/links)");

			lock (markupLock)
			{
				currentMarkup = new PersistedMarkup
				{
					Anchors = allAnchors,
					Relations = relations,
					SuppressedAnchorKeys = suppressed.ToList(),
					UserGroups = carriedUserGroups,
					Memberships = carriedMemberships,
					Links = carriedLinks,
					LostAnchors = carriedLost,
				};
				RebuildMarkupRootsUnsafe();
				ReloadNodesByIdUnsafe();
				if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var saveErr))
					Debug($"[listAnchors] cannot save anchors cache: {saveErr}");
				else
					Debug($"[listAnchors] saved persisted markup to {AnchorStore.GetStorePath(currentFolderPath)}");
			}

			return Task.FromResult(MakeListTreeResult(currentMarkup.Roots, null));
		}
	}
}
