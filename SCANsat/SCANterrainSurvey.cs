#region license
/*
 * [Scientific Committee on Advanced Navigation]
 * 			S.C.A.N. Satellite
 *
 * SCANterrainSurvey - authoring tool: measures a body's real height range from PQS and writes
 * the SCANSAT_TERRAIN nodes for a planet pack patch
 *
 * Copyright (c)2014 David Grandy <david.grandy@gmail.com>;
 * Copyright (c)2014 technogeeky <technogeeky@gmail.com>;
 * Copyright (c)2014 (Your Name Here) <your email here>; see LICENSE.txt for licensing details.
 */
#endregion

using SCANsat.SCAN_Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace SCANsat
{
	/// <summary>
	/// Authoring tool, not a runtime feature. A body's palette range comes from an authored
	/// SCANSAT_TERRAIN node, or failing that from pqsController.radiusMin/Max - and those are the
	/// template's numbers for a Kopernicus body built from one (on RSS, seven Saturn moons all report
	/// -1200..9900 m). This measures the real range so a pack can be authored properly.
	///
	/// Nothing runs unless a SCANSAT_TERRAIN_SURVEY node exists in GameDatabase, so drop the cfg in,
	/// start a game, wait for the log line, then delete it:
	///
	///   SCANSAT_TERRAIN_SURVEY
	///   {
	///       samplesPerDegree = 10   // 0.1 deg: 3600x1800 per body, a few seconds of CPU each
	///       msPerFrame = 30         // deliberately heavy - the game is slow while this runs
	///       body = Mimas            // optional, repeatable; omit for every body with a PQS
	///   }
	///
	/// It does NOT use the map builds' frame budget. That one exists to keep the game smooth while you
	/// play; here you are waiting on purpose, so it takes a big slice of the frame and says so. The
	/// whole RSS system at the defaults is a few minutes of sitting in the space centre.
	///
	/// Why not measure this at runtime: sampling can only miss extremes, never invent them, so the
	/// answer is a lower bound that improves with resolution. 1 degree is ~111 km at the equator and
	/// steps straight over Everest; 0.1 is ~11 km and lands on it. 3600x1800 is 6.5M PQS samples, far
	/// too much to pay on every launch and entirely reasonable to pay once while authoring.
	/// </summary>
	public static class SCANterrainSurvey
	{
		private class SurveyResult
		{
			internal string name;
			internal float min, max;
			internal double minLon, minLat, maxLon, maxLat;
			internal float seconds;
		}

		private const string surveyNodeName = "SCANSAT_TERRAIN_SURVEY";
		private const string outputFile = "SCANsat/PluginData/SCANsat_TerrainSurvey.cfg";

		// One row is sampled in chunks so a wide sweep cannot blow the frame budget in a single pass.
		private const int chunk = 512;

		private static readonly List<CelestialBody> queue = new List<CelestialBody>();
		private static readonly List<SurveyResult> results = new List<SurveyResult>();

		private static CelestialBody current;
		private static int samplesPerDegree = 10;
		private static double msPerFrame = 30;
		private static int row, col;
		private static float minH, maxH;
		private static double minLon, minLat, maxLon, maxLat;
		private static float bodyStart;
		private static bool started;

		internal static bool Running
		{
			get { return current != null || queue.Count > 0; }
		}

		/// <summary>
		/// Queue the survey if a SCANSAT_TERRAIN_SURVEY node asks for one. Once per game session.
		/// </summary>
		internal static void CheckForRequest()
		{
			if (started)
			{
				return;
			}

			started = true;

			ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes(surveyNodeName);

			if (nodes == null || nodes.Length == 0)
			{
				return;
			}

			List<string> named = new List<string>();

			for (int i = 0; i < nodes.Length; i++)
			{
				int perDegree;

				if (nodes[i].HasValue("samplesPerDegree") && int.TryParse(nodes[i].GetValue("samplesPerDegree"), out perDegree) && perDegree > 0)
				{
					samplesPerDegree = Math.Min(perDegree, 100);   // 0.01 deg is 648M samples; past that is a mistake, not a choice
				}

				double ms;

				if (nodes[i].HasValue("msPerFrame") && double.TryParse(nodes[i].GetValue("msPerFrame"), out ms) && ms > 0)
				{
					msPerFrame = Math.Min(ms, 200);
				}

				named.AddRange(nodes[i].GetValues("body"));
			}

			for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
			{
				CelestialBody b = FlightGlobals.Bodies[i];

				if (b == null || b.pqsController == null)
				{
					continue;   // gas giants and other PQS-less bodies have nothing to measure
				}

				if (named.Count > 0 && !named.Contains(b.bodyName))
				{
					continue;
				}

				queue.Add(b);
			}

			SCANUtil.SCANlog("Terrain survey requested: {0} bodies at {1} samples per degree ({2}x{3} each). Output goes to {4}",
				queue.Count, samplesPerDegree, 360 * samplesPerDegree, 180 * samplesPerDegree, outputFile);
		}

		/// <summary>
		/// One frame's worth, under the same shared CPU budget the map builds use.
		/// </summary>
		internal static void Pump()
		{
			if (current == null)
			{
				if (queue.Count == 0)
				{
					return;
				}

				current = queue[0];
				queue.RemoveAt(0);

				SCANcontroller.controller.loadPQS(current);

				row = 0;
				col = 0;
				minH = float.MaxValue;
				maxH = float.MinValue;
				minLon = minLat = maxLon = maxLat = 0;
				bodyStart = Time.realtimeSinceStartup;

				return;   // let the PQS finish loading before the first sample
			}

			long start = Stopwatch.GetTimestamp();
			long budget = (long)(msPerFrame * Stopwatch.Frequency / 1000.0);

			int rows = 180 * samplesPerDegree;
			int cols = 360 * samplesPerDegree;

			while (row < rows)
			{
				double lat = -90.0 + (row / (double)samplesPerDegree);
				int end = Math.Min(cols, col + chunk);

				for (; col < end; col++)
				{
					double lon = -180.0 + (col / (double)samplesPerDegree);
					float h = (float)SCANUtil.getElevation(current, lon, lat);

					if (h < minH)
					{
						minH = h;
						minLon = lon;
						minLat = lat;
					}

					if (h > maxH)
					{
						maxH = h;
						maxLon = lon;
						maxLat = lat;
					}
				}

				if (col >= cols)
				{
					col = 0;
					row++;
				}

				if (Stopwatch.GetTimestamp() - start >= budget)
				{
					break;
				}
			}

			if (row < rows)
			{
				return;
			}

			finishBody();
		}

		private static void finishBody()
		{
			SurveyResult r = new SurveyResult()
			{
				name = current.bodyName,
				min = minH,
				max = maxH,
				minLon = minLon,
				minLat = minLat,
				maxLon = maxLon,
				maxLat = maxLat,
				seconds = Time.realtimeSinceStartup - bodyStart
			};

			results.Add(r);

			logResult(r);

			SCANcontroller.controller.unloadPQS(current);
			current = null;

			if (queue.Count == 0)
			{
				writeOutput();
			}
		}

		/// <summary>
		/// Log the measurement next to the range the body would otherwise have used, which is the whole
		/// point of running this: it says which bodies actually need authoring.
		/// </summary>
		private static void logResult(SurveyResult r)
		{
			string was = "";

			if (SCANcontroller.hasTerrainNode(r.name))
			{
				SCANterrainConfig t = SCANcontroller.getTerrainNode(r.name);

				if (t != null)
				{
					was = string.Format(" (config says {0:F0} to {1:F0}{2})", t.MinTerrain, t.MaxTerrain, t.AutoGenerated ? ", guessed from PQS" : ", authored");
				}
			}

			SCANUtil.SCANlog("[{0}] surveyed {1:F0} m to {2:F0} m{3}; min at {4:F2} lat {5:F2} lon, max at {6:F2} lat {7:F2} lon, {8:F1} s",
				r.name, r.min, r.max, was, r.minLat, r.minLon, r.maxLat, r.maxLon, r.seconds);
		}

		private static void writeOutput()
		{
			StringBuilder sb = new StringBuilder();

			sb.AppendLine("// SCANsat terrain ranges, measured from PQS by SCANterrainSurvey.");
			sb.AppendFormat("// Sampled at {0} samples per degree ({1}x{2} per body).", samplesPerDegree, 360 * samplesPerDegree, 180 * samplesPerDegree);
			sb.AppendLine();
			sb.AppendLine("// Ranges are rounded outward to 100 m. Add a :NEEDS[] guard for the pack before shipping,");
			sb.AppendLine("// and sanity check the extremes: the max for a body you recognise should land on the feature");
			sb.AppendLine("// you expect (Earth's on Everest, at about 28 lat 87 lon).");
			sb.AppendLine();

			for (int i = 0; i < results.Count; i++)
			{
				SurveyResult r = results[i];

				sb.AppendLine("SCANSAT_TERRAIN");
				sb.AppendLine("{");
				sb.AppendFormat("\tname = {0}", r.name);
				sb.AppendLine();
				sb.AppendFormat("\tminHeightRange = {0:F0}", Mathf.Floor(r.min / 100f) * 100f);
				sb.AppendLine();
				sb.AppendFormat("\tmaxHeightRange = {0:F0}", Mathf.Ceil(r.max / 100f) * 100f);
				sb.AppendLine();
				sb.AppendFormat("\t// measured {0:F0} to {1:F0}; max at {2:F2} lat {3:F2} lon", r.min, r.max, r.maxLat, r.maxLon);
				sb.AppendLine();
				sb.AppendLine("}");
			}

			string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/" + outputFile).Replace("\\", "/");

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, sb.ToString());
				SCANUtil.SCANlog("Terrain survey complete: {0} bodies written to {1}", results.Count, path);
			}
			catch (Exception e)
			{
				SCANUtil.SCANlog("Terrain survey could not write {0}: {1}\nThe per-body results are in the log above.", path, e);
			}

			results.Clear();
		}
	}
}
