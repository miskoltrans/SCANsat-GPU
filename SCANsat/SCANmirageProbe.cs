#region license
/*
 * [Scientific Committee on Advanced Navigation]
 * 			S.C.A.N. Satellite
 *
 * SCANmirageProbe - EXPERIMENT, not for release: does PQS altimetry work on Mirage bodies?
 */
#endregion

using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace SCANsat
{
	/// <summary>
	/// Samples every Mirage body through the same PQS call SCANsat's altimetry uses and logs what comes back:
	/// a 5-degree grid (range, unresolved samples, cold and warm timing) and spot heights at known landmarks,
	/// which check the values and the orientation at once.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.TrackingStation, false)]
	public class SCANmirageProbe : MonoBehaviour
	{
		private struct Landmark
		{
			public string body, name;
			public double lat, lon, expected;

			public Landmark(string body, string name, double lat, double lon, double expected)
			{
				this.body = body;
				this.name = name;
				this.lat = lat;
				this.lon = lon;
				this.expected = expected;
			}
		}

		// Real-world elevations (m) against each body's usual datum; longitudes east-positive, as KSP.
		private static readonly Landmark[] landmarks =
		{
			new Landmark("Earth", "Everest", 27.9881, 86.9250, 8849),
			new Landmark("Earth", "Challenger Deep", 11.3733, 142.5917, -10935),
			new Landmark("Earth", "Tibetan Plateau (Lhasa)", 29.65, 91.10, 3650),
			new Landmark("Earth", "Amazon (Manaus)", -3.10, -60.02, 90),
			new Landmark("Moon", "Selenean summit", 5.4125, -158.6335, 10786),
			new Landmark("Moon", "Antoniadi floor", -70.4, -172.6, -9178),
			new Landmark("Mars", "Olympus Mons", 18.65, -133.8, 21229),
			new Landmark("Mars", "Hellas floor", -42.4, 70.5, -7152),
		};

		private IEnumerator Start()
		{
			yield return new WaitForSeconds(2f);

			foreach (CelestialBody body in FlightGlobals.Bodies)
			{
				if (SCANUtil.mirageTerrainMod(body) == null)
				{
					continue;
				}

				probeGrid(body);
				yield return null;
			}

			foreach (Landmark mark in landmarks)
			{
				CelestialBody body = FlightGlobals.GetBodyByName(mark.body);
				if (body == null || body.pqsController == null)
				{
					continue;
				}

				double h = height(body, mark.lat, mark.lon);
				SCANUtil.SCANlog("[MirageProbe] {0} {1} ({2:F2}, {3:F2}): {4:F0} m, expected ~{5:F0} m, diff {6:F0} m",
					mark.body, mark.name, mark.lat, mark.lon, h, mark.expected, h - mark.expected);
			}

			SCANUtil.SCANlog("[MirageProbe] done");
		}

		private static void probeGrid(CelestialBody body)
		{
			PQS pqs = body.pqsController;
			SCANUtil.mirageHeightRange(body, out double spanMin, out double spanMax);
			SCANUtil.SCANlog("[MirageProbe] {0}: radius {1:F0}, radiusMin-r {2:F1}, radiusMax-r {3:F1}, Mirage span {4:F0}..{5:F0} m, warp {6}",
				body.bodyName, pqs.radius, pqs.radiusMin - pqs.radius, pqs.radiusMax - pqs.radius, spanMin, spanMax, TimeWarp.CurrentRate);

			for (int pass = 0; pass < 2; pass++)
			{
				double min = double.MaxValue, max = double.MinValue, sum = 0;
				int n = 0, invalid = 0, zero = 0;
				Stopwatch watch = Stopwatch.StartNew();

				for (double lat = -87.5; lat < 90; lat += 5)
				{
					for (double lon = -177.5; lon < 180; lon += 5)
					{
						double h = height(body, lat, lon);
						if (double.IsNaN(h) || double.IsInfinity(h))
						{
							invalid++;
							continue;
						}

						if (h == 0)
						{
							zero++;
						}

						min = Math.Min(min, h);
						max = Math.Max(max, h);
						sum += h;
						n++;
					}
				}

				watch.Stop();
				SCANUtil.SCANlog("[MirageProbe] {0} grid pass {1} ({2}): {3} samples in {4} ms ({5:F3} ms each); min {6:F0} m, max {7:F0} m, mean {8:F0} m; exactly 0: {9}; NaN/inf: {10}",
					body.bodyName, pass + 1, pass == 0 ? "cold" : "warm", n + invalid, watch.ElapsedMilliseconds,
					watch.Elapsed.TotalMilliseconds / Math.Max(1, n + invalid), n > 0 ? min : 0, n > 0 ? max : 0, n > 0 ? sum / n : 0, zero, invalid);
			}
		}

		// The same direction convention and PQS call as SCANUtil.getElevation, minus its validity gate and rounding.
		private static double height(CelestialBody body, double lat, double lon)
		{
			double rlat = Mathf.Deg2Rad * lat;
			double rlon = Mathf.Deg2Rad * lon;
			Vector3d rad = new Vector3d(Math.Cos(rlat) * Math.Cos(rlon), Math.Sin(rlat), Math.Cos(rlat) * Math.Sin(rlon));
			return body.pqsController.GetSurfaceHeight(rad) - body.pqsController.radius;
		}
	}
}
