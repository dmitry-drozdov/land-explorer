using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Markup
{
	public struct Similarity
	{
		public double HeaderNonCoreSimilarity { get; set; }
		public double HeaderCoreSimilarity { get; set; }

		public double AncestorSimilarity { get; set; }
		public double InnerSimilarity { get; set; }
	}
}
