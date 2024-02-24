using Land.Markup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	internal class ResourceStats
	{
		public float ParseGraphql;
		public float AddGraphqlConcern;
		public float ParseGoTotal;
		public float ParseGoPre;
		public float ParseGoMain;
		public float ParseGoPost;
		public float VisitGo;
		public float AddGoConcern;
		private Stopwatch watch;
		private long maxMemoryUsage = 0;
		private long initialMemory = 0;

		public void Start()
		{
			watch = Stopwatch.StartNew();
			if (initialMemory == 0)
			{
				GC.Collect();
				initialMemory = Process.GetCurrentProcess().PrivateMemorySize64;
			}
		}
		public void Stop(ref float val)
		{
			watch.Stop();
			val += watch.ElapsedMilliseconds;

			var m = Process.GetCurrentProcess().PrivateMemorySize64;
			if (m > maxMemoryUsage) maxMemoryUsage = m;
		}

		public override string ToString()
		{
			return $"graphql [parse: {Format(ParseGraphql)}, add concern: {Format(AddGraphqlConcern)}] " +
				$"go [parse {Format(ParseGoTotal)}={Format(ParseGoPre)}+{Format(ParseGoMain)}+{Format(ParseGoPost)}, " +
					$"visit {Format(VisitGo)}, add concern {Format(AddGoConcern)}] " +
				$"[{(maxMemoryUsage - initialMemory) / (1024 * 1024)}]";
		}

		private string Format(float val)
		{
			if (val >= 1000)
			{
				return $"{val / 1000: 0.0} s";
			}
			return $"{val / 1000: 0.00} s";
		}
	}
}
