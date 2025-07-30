using Land.Core.Parsing.Tree;
using Land.Markup.Binding;
using Land.Markup.CoreExtension;
using Land.Markup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Land.Control.ControlPartial
{
	internal class Cache
	{
		public Dictionary<string, GroupNodesByTypeVisitor> visitorCache;
		public Dictionary<Node, SiblingsContextConstructionCache> ancestorToSiblingsCache;
		public Dictionary<Node, PointContext> pointContextCntOnlyCache;
		public Dictionary<Node, SiblingsContext> siblingContextCache;
		public ConcurrentDictionary<CommutativePairGuid, Similarity> similarityCache;

		public Cache()
		{
			this.visitorCache = new Dictionary<string, GroupNodesByTypeVisitor>();
			this.ancestorToSiblingsCache = new Dictionary<Node, SiblingsContextConstructionCache>();
			this.pointContextCntOnlyCache = new Dictionary<Node, PointContext>();
			this.siblingContextCache = new Dictionary<Node, SiblingsContext>();
			this.similarityCache = new ConcurrentDictionary<CommutativePairGuid, Similarity>();
		}
	}
}
