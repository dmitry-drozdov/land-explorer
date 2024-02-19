using Land.Markup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	internal class Durations
	{
		public float ParseGraphql;
		public float AddGraphqlConcern;
		public float ParseGo;
		public float VisitGo;
		public float AddGoConcern;
		private Stopwatch watch;
		public void Start()
		{
			watch = Stopwatch.StartNew();
		}
		public void Stop(ref float val)
		{
			watch.Stop();
			val += watch.ElapsedMilliseconds;
		}

		public override string ToString()
		{
			return $"graphql [parse: {format(ParseGraphql)}, add concern: {format(AddGraphqlConcern)}] " +
				$"go [parse {format(ParseGo)}, visit {format(VisitGo)}, add concern {format(AddGoConcern)}]";
		}

		private string format(float val)
		{
			if (val >= 1000)
			{
				return $"{val/1000 : 0.0} s";
			}
			return $"{val} ms";
		}

	}
}
