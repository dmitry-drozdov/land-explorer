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

			BuildSemanticMarkupFromDisk(out var gqlAnchors, out var tsAnchors);
			var relations = BuildRelations(gqlAnchors, tsAnchors);
			Debug($"gqlAnchors={gqlAnchors.Count}, tsAnchors={tsAnchors.Count}, relations={relations.Count}");

			lock (markupLock)
			{
				currentMarkup = new PersistedMarkup
				{
					Anchors = gqlAnchors.Select(CloneAnchor).Concat(tsAnchors.Select(CloneAnchor)).ToList(),
					Relations = relations,
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
