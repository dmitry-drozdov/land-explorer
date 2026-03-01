using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		// Текущий snapshot разметки (Roots), загруженный/созданный для currentFolderPath.
		// Нужен, чтобы updateAnchor мог обновлять файл на диске.
		private PersistedMarkup currentMarkup;
		private readonly object markupLock = new object();

		private BaseParser graphqlParser;
		private BaseParser typescriptParser;
		// Все узлы, которые мы отдали в дерево (anchor'ы нужны для updateAnchor).
		// ВАЖНО: ключ должен быть глобально уникальным на весь folder (иначе коллизии при нескольких файлах).
		private Dictionary<string, TreeNode> nodesById = [];

		// Кэш последнего построенного дерева для папки (чтобы updateAnchor работал на весь folder,
		// и не было жёстко зашитых путей).
		private string currentFolderPath;
		private List<MethodAnchor> currentGqlAnchors = new();
		private List<TreeNode> currentGqlNodes = new();
		private VPTree<MethodAnchor> currentTree;
		private Dist.Weights currentWeights = new();

		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
