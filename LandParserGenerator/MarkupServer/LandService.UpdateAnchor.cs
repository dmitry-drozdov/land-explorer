using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		private void BuildCurrentRebindingForestsFromDisk()
		{
			if (string.IsNullOrWhiteSpace(currentFolderPath))
				throw new InvalidOperationException("currentFolderPath is empty. Call land/listAnchors first.");

			currentContextsByProfileKey.Clear();
			currentNodesByProfileKey.Clear();
			currentTreesByProfileKey.Clear();

			BuildSemanticMarkupFromDisk(out var gqlAnchors, out var tsAnchors);
			var allAnchors = gqlAnchors.Concat(tsAnchors).Where(CanParticipateInRebinding).ToList();

			foreach (var grp in allAnchors.GroupBy(GetProfileKey, StringComparer.OrdinalIgnoreCase))
			{
				var nodes = grp.Select(CloneAnchor).ToList();
				var contexts = nodes.Select(BuildAnchorContext).Where(x => x != null).ToList();
				currentNodesByProfileKey[grp.Key] = nodes;
				currentContextsByProfileKey[grp.Key] = contexts;
				if (contexts.Count == 0)
					continue;

				var weights = GetWeightsForProfileKey(grp.Key);
				var tree = new VPTree<AnchorContext>(
					contexts,
					(a, b) => AnchorContextDistance(a, b, weights),
					42,
					Tracing.Tracer);

				currentTreesByProfileKey[grp.Key] = tree;

				Debug($"[vptree] built profile={grp.Key}, size={contexts.Count}, depth={tree.BuildDepth}");
			}
		}

		[JsonRpcMethod("land/updateAnchor", UseSingleObjectParameterDeserialization = true)]
		public Task<UpdateAnchorResultV2> UpdateAnchorAsync(UpdateAnchorParams p)
		{
			if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
				throw new ArgumentException("anchorId is required");

			if (!nodesById.TryGetValue(p.anchorId, out var oldNode) || oldNode == null)
			{
				Debug($"not found anchor by id [{p.anchorId}] (cache lost). Please run land/listAnchors again.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			EnsureAnchorFamily(oldNode);
			var profileKey = GetProfileKey(oldNode);
			if (string.IsNullOrWhiteSpace(profileKey))
			{
				Debug($"[updateAnchor] anchor [{p.anchorId}] has unsupported rebind profile.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			try
			{
				BuildCurrentRebindingForestsFromDisk();
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cannot rebuild rebinding forest: {ex.Message}");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			if (!currentTreesByProfileKey.TryGetValue(profileKey, out var tree)
				|| !currentContextsByProfileKey.TryGetValue(profileKey, out var contexts)
				|| !currentNodesByProfileKey.TryGetValue(profileKey, out var nodes)
				|| contexts.Count == 0)
			{
				Debug($"[updateAnchor] no index for profile='{profileKey}'.");
				return Task.FromResult(MakeUpdateAnchorResult(null));
			}

			var query = BuildAnchorContext(oldNode);
			var cands = tree.KNearest(query, 1);
			if (cands == null || cands.Count == 0)
				return Task.FromResult(MakeUpdateAnchorResult(null));

			var best = cands[0];
			var newNode = nodes[best.Index];
			Debug($"[updateAnchor] profile={profileKey} dist={best.Dist} -> {newNode?.Filepath}:{newNode?.StartOffset}-{newNode?.EndOffset} {newNode?.Name}");

			var updated = CloneAnchor(newNode);
			updated.Id = p.anchorId;
			updated.AnchorFamily = EnsureAnchorFamily(updated);

			var isGql = string.Equals(updated.Language, LangGql, StringComparison.OrdinalIgnoreCase);
			var parentGroupId = isGql ? GetTypeGroupId(updated) : null;
			var parentGroupName = isGql ? GetTypeGroupName(updated) : null;
			var fieldGroupId = isGql ? GetFieldGroupId(updated) : null;
			var fieldGroupName = isGql ? GetFieldGroupName(updated) : null;

			nodesById[p.anchorId] = updated;

			try
			{
				lock (markupLock)
				{
					ReplaceAnchorInMarkupUnsafe(updated);
					RebuildMarkupRootsUnsafe();
					ReloadNodesByIdUnsafe();
					if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
						Debug($"[updateAnchor] cannot save anchors cache: {err}");
				}
			}
			catch (Exception ex)
			{
				Debug($"[updateAnchor] cache persist failed: {ex.Message}");
			}

			return Task.FromResult(MakeUpdateAnchorResult(updated, parentGroupId, parentGroupName, fieldGroupId, fieldGroupName));
		}
	}
}
