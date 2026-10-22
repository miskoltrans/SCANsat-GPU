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
using System.Reflection;
using UnityEngine;

namespace SCANsat
{
	/// <summary>
	/// Samples every Mirage body through the same PQS call SCANsat's altimetry uses and logs what comes back:
	/// a 5-degree grid (range, unresolved samples, cold and warm timing), spot heights at known landmarks with
	/// the nearest local extreme, and each landmark body's 1-degree extremes. It also checks Mirage's offline
	/// cube math against the projection the terrain is built with, and re-samples once at on-rails warp,
	/// where Mirage drops direction queries to a coarse level.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.TrackingStation, false)]
	public class SCANmirageProbe : MonoBehaviour
	{
		private struct Landmark
		{
			public string body, name;
			public double lat, lon, expected;
			// +1: search the box for the highest point, -1: the lowest, 0: no search.
			public int extreme;

			public Landmark(string body, string name, double lat, double lon, double expected, int extreme)
			{
				this.body = body;
				this.name = name;
				this.lat = lat;
				this.lon = lon;
				this.expected = expected;
				this.extreme = extreme;
			}
		}

		// Real-world elevations (m) against each body's usual datum; longitudes east-positive, as KSP.
		// Sol measures Mars from the 3375.8 km RSS datum, not the areoid, so its expectations are relative only.
		private static readonly Landmark[] landmarks =
		{
			new Landmark("Earth", "Everest", 27.9881, 86.9250, 8849, 1),
			new Landmark("Earth", "Challenger Deep", 11.3733, 142.5917, -10935, -1),
			new Landmark("Earth", "Tibetan Plateau (Lhasa)", 29.65, 91.10, 3650, 0),
			new Landmark("Earth", "Amazon (Manaus)", -3.10, -60.02, 90, 0),
			new Landmark("Moon", "Selenean summit", 5.4125, -158.6335, 10786, 1),
			new Landmark("Moon", "Antoniadi floor", -70.4, -172.6, -9178, -1),
			new Landmark("Mars", "Olympus Mons", 18.65, -133.8, 21229, 1),
			new Landmark("Mars", "Hellas floor", -42.4, 70.5, -7152, -1),
		};

		private static readonly string[] extremeBodies = { "Earth", "Moon", "Mars" };

		private const double SearchHalfBox = 5;
		private const double SearchStep = 0.1;

		private bool initialDone, warpProbed;

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

			checkCubeMapping();
			yield return null;

			foreach (Landmark mark in landmarks)
			{
				probeLandmark(mark, true);
				yield return null;
			}

			foreach (string name in extremeBodies)
			{
				CelestialBody body = FlightGlobals.GetBodyByName(name);
				if (body != null && body.pqsController != null)
				{
					globalExtremes(body);
				}
				yield return null;
			}

			SCANUtil.SCANlog("[MirageProbe] done; time-warp once to re-sample at Mirage's on-rails level");
			initialDone = true;
		}

		private void Update()
		{
			if (!initialDone || warpProbed || TimeWarp.CurrentRate <= 1f || TimeWarp.WarpMode != TimeWarp.Modes.HIGH)
			{
				return;
			}

			warpProbed = true;
			SCANUtil.SCANlog("[MirageProbe] warp pass at {0}x", TimeWarp.CurrentRate);

			foreach (string name in extremeBodies)
			{
				CelestialBody body = FlightGlobals.GetBodyByName(name);
				if (body != null && body.pqsController != null)
				{
					gridPass(body, "warp");
				}
			}

			foreach (Landmark mark in landmarks)
			{
				probeLandmark(mark, false);
			}

			SCANUtil.SCANlog("[MirageProbe] warp pass done");
		}

		private static void probeGrid(CelestialBody body)
		{
			PQS pqs = body.pqsController;
			SCANUtil.mirageHeightRange(body, out double spanMin, out double spanMax);
			SCANUtil.SCANlog("[MirageProbe] {0}: radius {1:F0}, radiusMin-r {2:F1}, radiusMax-r {3:F1}, Mirage span {4:F0}..{5:F0} m, height MaxLevel {6}, warp {7}",
				body.bodyName, pqs.radius, pqs.radiusMin - pqs.radius, pqs.radiusMax - pqs.radius, spanMin, spanMax, heightMaxLevel(body), TimeWarp.CurrentRate);

			gridPass(body, "cold");
			gridPass(body, "warm");
		}

		private static void gridPass(CelestialBody body, string label)
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
			SCANUtil.SCANlog("[MirageProbe] {0} grid ({1}): {2} samples in {3} ms ({4:F3} ms each); min {5:F0} m, max {6:F0} m, mean {7:F0} m; exactly 0: {8}; NaN/inf: {9}",
				body.bodyName, label, n + invalid, watch.ElapsedMilliseconds,
				watch.Elapsed.TotalMilliseconds / Math.Max(1, n + invalid), n > 0 ? min : 0, n > 0 ? max : 0, n > 0 ? sum / n : 0, zero, invalid);
		}

		private static void probeLandmark(Landmark mark, bool search)
		{
			CelestialBody body = FlightGlobals.GetBodyByName(mark.body);
			if (body == null || body.pqsController == null)
			{
				return;
			}

			double h = height(body, mark.lat, mark.lon);
			SCANUtil.SCANlog("[MirageProbe] {0} {1} ({2:F2}, {3:F2}): {4:F0} m, expected ~{5:F0} m, diff {6:F0} m",
				mark.body, mark.name, mark.lat, mark.lon, h, mark.expected, h - mark.expected);

			if (!search || mark.extreme == 0)
			{
				return;
			}

			double best = mark.extreme > 0 ? double.MinValue : double.MaxValue;
			double bestLat = 0, bestLon = 0;

			for (double lat = mark.lat - SearchHalfBox; lat <= mark.lat + SearchHalfBox + 1e-9; lat += SearchStep)
			{
				for (double lon = mark.lon - SearchHalfBox; lon <= mark.lon + SearchHalfBox + 1e-9; lon += SearchStep)
				{
					double s = height(body, lat, lon);
					if (mark.extreme > 0 ? s > best : s < best)
					{
						best = s;
						bestLat = lat;
						bestLon = lon;
					}
				}
			}

			SCANUtil.SCANlog("[MirageProbe]   {0} within +/-{1} deg: {2:F0} m at ({3:F2}, {4:F2}), {5:F0} km from the landmark",
				mark.extreme > 0 ? "highest" : "lowest", SearchHalfBox, best, bestLat, bestLon,
				greatCircleKm(body, mark.lat, mark.lon, bestLat, bestLon));
		}

		private static void globalExtremes(CelestialBody body)
		{
			double min = double.MaxValue, max = double.MinValue;
			double minLat = 0, minLon = 0, maxLat = 0, maxLon = 0;
			Stopwatch watch = Stopwatch.StartNew();

			for (double lat = -89.5; lat < 90; lat += 1)
			{
				for (double lon = -179.5; lon < 180; lon += 1)
				{
					double h = height(body, lat, lon);
					if (h < min)
					{
						min = h;
						minLat = lat;
						minLon = lon;
					}

					if (h > max)
					{
						max = h;
						maxLat = lat;
						maxLon = lon;
					}
				}
			}

			watch.Stop();
			SCANUtil.SCANlog("[MirageProbe] {0} 1-deg extremes ({1} ms): highest {2:F0} m at ({3:F1}, {4:F1}), lowest {5:F0} m at ({6:F1}, {7:F1})",
				body.bodyName, watch.ElapsedMilliseconds, max, maxLat, maxLon, min, minLat, minLon);
		}

		// The terrain is built from KSP's gnomonic face coordinates; an offline reader (Azrail's notebooks, a future
		// archive source) uses Mirage's measured cube basis instead. They must agree for its maps to line up.
		private static void checkCubeMapping()
		{
			CelestialBody anyMirage = null;
			foreach (CelestialBody body in FlightGlobals.Bodies)
			{
				if (SCANUtil.mirageTerrainMod(body) != null)
				{
					anyMirage = body;
					break;
				}
			}

			if (anyMirage == null)
			{
				return;
			}

			Type cubeMath = SCANUtil.mirageTerrainMod(anyMirage).GetType().Assembly.GetType("Mirage.WebIngest.MirageCubeMath");
			MethodInfo toFaceUV = cubeMath?.GetMethod("DirectionToFaceUV", BindingFlags.Public | BindingFlags.Static);
			if (toFaceUV == null)
			{
				SCANUtil.SCANlog("[MirageProbe] cube mapping: MirageCubeMath.DirectionToFaceUV not found");
				return;
			}

			System.Random rng = new System.Random(1);
			object[] args = new object[6];
			int samples = 20000, faceMismatch = 0, shown = 0;
			double maxDu = 0, maxDv = 0;

			for (int i = 0; i < samples; i++)
			{
				double y = 2 * rng.NextDouble() - 1;
				double theta = 2 * Math.PI * rng.NextDouble();
				double r = Math.Sqrt(1 - y * y);
				Vector3d dir = new Vector3d(r * Math.Cos(theta), y, r * Math.Sin(theta));

				PQSMod_GnomonicTest.GetGnomonicMapCoords(dir, out PQS.QuadPlane plane, out double u, out double v);

				args[0] = dir.x;
				args[1] = dir.y;
				args[2] = dir.z;
				toFaceUV.Invoke(null, args);
				int face = (int)args[3];
				double mu = (double)args[4], mv = (double)args[5];

				double du = Math.Abs(u - mu), dv = Math.Abs(v - mv);
				bool faceDiffers = face != (int)plane;
				if (faceDiffers)
				{
					faceMismatch++;
				}
				else
				{
					maxDu = Math.Max(maxDu, du);
					maxDv = Math.Max(maxDv, dv);
				}

				if ((faceDiffers || du > 1e-6 || dv > 1e-6) && shown < 6)
				{
					shown++;
					SCANUtil.SCANlog("[MirageProbe]   dir ({0:F4}, {1:F4}, {2:F4}): KSP face {3} uv ({4:F5}, {5:F5}), Mirage face {6} uv ({7:F5}, {8:F5})",
						dir.x, dir.y, dir.z, (int)plane, u, v, face, mu, mv);
				}
			}

			SCANUtil.SCANlog("[MirageProbe] cube mapping: {0} directions, face mismatches {1}, max |du| {2:E2}, max |dv| {3:E2} (same face only)",
				samples, faceMismatch, maxDu, maxDv);
		}

		private static int heightMaxLevel(CelestialBody body)
		{
			PQSMod mod = SCANUtil.mirageTerrainMod(body);
			object layer = mod?.GetType().GetProperty("HeightLayer")?.GetValue(mod, null);
			object level = layer?.GetType().GetProperty("MaxLevel")?.GetValue(layer, null);
			return level is int l ? l : -1;
		}

		private static double greatCircleKm(CelestialBody body, double lat1, double lon1, double lat2, double lon2)
		{
			double p1 = Mathf.Deg2Rad * lat1, p2 = Mathf.Deg2Rad * lat2, dl = Mathf.Deg2Rad * (lon2 - lon1);
			double c = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl);
			return Math.Acos(Math.Max(-1, Math.Min(1, c))) * body.Radius / 1000;
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
