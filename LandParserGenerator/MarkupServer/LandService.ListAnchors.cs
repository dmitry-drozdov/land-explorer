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
			// Memberships фильтруем ТОЛЬКО по anchorId — висячие auto-group-Id оставляем,
			// чтобы membership автоматически "ожил", если auto-группа вернётся
			// (например, при откате переименования метода). Рендеринг
			// (BuildRootsFromMarkup/AppendMembershipShadows) молча игнорирует
			// memberships без подходящей группы.
			var carriedUserGroups = (previousMarkup?.UserGroups ?? new List<UserGroup>())
				.Where(g => g != null && !string.IsNullOrWhiteSpace(g.Id))
				.ToList();
			var validAnchorIds = new HashSet<string>(allAnchors.Where(a => !string.IsNullOrWhiteSpace(a?.Id)).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

			var previousMemberships = previousMarkup?.Memberships ?? new List<UserGroupMembership>();
			var carriedMemberships = previousMemberships
				.Where(m => m != null
					&& !string.IsNullOrWhiteSpace(m.GroupId)
					&& !string.IsNullOrWhiteSpace(m.AnchorId)
					&& validAnchorIds.Contains(m.AnchorId))
				.ToList();
			var droppedMemberships = previousMemberships.Count - carriedMemberships.Count;
			if (droppedMemberships > 0)
				Debug($"[listAnchors] dropped {droppedMemberships} memberships whose anchor disappeared");

			lock (markupLock)
			{
				currentMarkup = new PersistedMarkup
				{
					Anchors = allAnchors,
					Relations = relations,
					SuppressedAnchorKeys = suppressed.ToList(),
					UserGroups = carriedUserGroups,
					Memberships = carriedMemberships,
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
