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

			lock (markupLock)
			{
				currentMarkup = new PersistedMarkup
				{
					Anchors = allAnchors,
					Relations = relations,
					SuppressedAnchorKeys = suppressed.ToList(),
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
