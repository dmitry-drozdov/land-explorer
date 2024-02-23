using System;
using System.Collections.Generic;
using System.Linq;

using Land.Core;
using Land.Core.Parsing.Tree;
using Land.Markup.Binding;

namespace Land.Control
{
	public abstract class ConcernPointCandidate
	{
		public string ViewHeader { get; set; }

		public string NormalizedName { get; set; }

		public override string ToString()
		{
			return ViewHeader;
		}
	}

	public class StringConcernPointCandidate : ExistingConcernPointCandidate
	{
		public StringConcernPointCandidate() : base() { }
	}

	public class ExistingConcernPointCandidate : ConcernPointCandidate
	{
		public Node Node { get; set; }

		public SegmentLocation Line { get; set; }

		public ExistingConcernPointCandidate() { }

		public ExistingConcernPointCandidate(Node node)
		{
			Node = node;
			ViewHeader = $"{node.Type}: {String.Join(" ", PointContext.GetHeaderContext(node).Sequence_old)}";
			NormalizedName = ToString().
				Replace("func_line: ", "").
				Replace("type_line: ", "").
				Replace(" :", "").
				Replace("_", "").
				ToLower(); // graphql
		}
	}

	public class CustomConcernPointCandidate : ConcernPointCandidate
	{
		public SegmentLocation RealSelection { get; set; }
		public SegmentLocation AdjustedSelection { get; set; }

		public CustomConcernPointCandidate(SegmentLocation realSelection,
			SegmentLocation adjustedSelection, string viewHeader)
		{
			RealSelection = realSelection;
			AdjustedSelection = adjustedSelection;
			ViewHeader = viewHeader;
		}
	}

	public class GraphqlConcernPointCandidates
	{
		public List<ConcernPointCandidate> Funcs { get; set; }
		public List<ConcernPointCandidate> Types { get; set; }
		public GraphqlConcernPointCandidates(List<ConcernPointCandidate> funcs, List<ConcernPointCandidate> types)
		{
			Funcs = funcs;
			Types = types;
		}
	}
}
