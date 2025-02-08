using Land.Markup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	public class ResourceStats
	{

		private Dictionary<string, Stopwatch> timers = new Dictionary<string, Stopwatch>();
		private Dictionary<string, float> times = new Dictionary<string, float>();

		public void Start(string name)
		{
			if (!timers.ContainsKey(name))
			{
				timers.Add(name, new Stopwatch());
			}
			timers[name].Reset();
			timers[name].Start();
		}
		public void Stop(string name)
		{
			timers[name].Stop();
			if (!times.ContainsKey(name))
			{
				times.Add(name, 0);
			}
			times[name] += timers[name].ElapsedMilliseconds;
		}

		public override string ToString()
		{
			var s = "";
			foreach (var item in times)
			{
				s += $"{item.Key}: {item.Value: 0}ms; ";
			}
			return s;
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
