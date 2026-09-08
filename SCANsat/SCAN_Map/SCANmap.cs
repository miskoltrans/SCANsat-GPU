#region license
/*
 * [Scientific Committee on Advanced Navigation]
 * 			S.C.A.N. Satellite
 * 
 * SCANmap - makes maps from data
 *
 * Copyright (c)2013 damny;
 * Copyright (c)2014 technogeeky <technogeeky@gmail.com>;
 * Copyright (c)2014 (Your Name Here) <your email here>; see LICENSE.txt for licensing details.
*/
#endregion

using System;
using System.Linq;
using System.IO;
using UnityEngine;
using SCANsat.SCAN_Platform.Palettes;
using SCANsat.SCAN_Data;
using SCANsat.SCAN_UI.UI_Framework;
using SCANsat.SCAN_Unity;
using palette = SCANsat.SCAN_UI.UI_Framework.SCANcolorUtil;

namespace SCANsat.SCAN_Map
{
	public class SCANmap
	{
		internal SCANmap(CelestialBody Body, bool Cache, mapSource s)
		{
			body = Body;
			mSource = s;
			pqs = body.pqsController != null;
			biomeMap = body.BiomeMap != null;
			data = SCANUtil.getData(body);
			if (data == null)
			{
				data = new SCANdata(body);
				SCANcontroller.controller.addToBodyData(body, data);
			}
			cache = Cache;
		}

		internal SCANmap()
		{
		}

		#region Public Accessors

		public double MapScale
		{
			get { return mapscale; }
			internal set
			{
				mapscale = value;
				resourceMapScale = (mapwidth / resourceMapWidth) * mapscale;
			}
		}

		public double Lon_Offset
		{
			get { return lon_offset; }
		}

		public double Lat_Offset
		{
			get { return lat_offset; }
		}

		public double CenteredLong
		{
			get { return centeredLong; }
		}

		public double CenteredLat
		{
			get { return centeredLat; }
		}

		public int MapWidth
		{
			get { return mapwidth; }
		}

		public int MapHeight
		{
			get { return mapheight; }
		}

		public mapType MType
		{
			get { return mType; }
			set { mType = value; }
		}

		public bool ColorMap
		{
			get { return colorMap; }
			set { colorMap = value; }
		}

		public bool Terminator
		{
			get { return terminator; }
			set { terminator = value; }
		}

		public mapSource MSource
		{
			get { return mSource; }
		}

		public Texture2D Map
		{
			get { return map; }
			internal set { map = value; }
		}

		// The texture to display. A GPU-rendered Visual map is a RenderTexture (accepted by
		// RawImage.texture / Graphics.Blit); otherwise the CPU-built Texture2D. Use this for
		// on-screen display; use Map (Texture2D) for CPU readback such as PNG export.
		public Texture DisplayTexture
		{
			get { return gpuRendered && visualRenderTex != null ? (Texture)visualRenderTex : (Texture)map; }
		}

		public bool GpuRendered
		{
			get { return gpuRendered; }
		}

		public RenderTexture VisualRenderTexture
		{
			get { return visualRenderTex; }
		}

		public CelestialBody Body
		{
			get { return body; }
		}

		public SCANresourceGlobal Resource
		{
			get { return resource; }
			set { resource = value; }
		}

		public bool ResourceActive
		{
			get { return resourceActive; }
			set { resourceActive = value; }
		}

		public SCANmapLegend MapLegend
		{
			get { return mapLegend; }
			internal set { mapLegend = value; }
		}

		public MapProjection Projection
		{
			get { return projection; }
			set { projection = value; }
		}

		internal float[,] Big_HeightMap
		{
			get { return big_heightmap; }
		}

		public bool UseCustomRange
		{
			get { return useCustomRange; }
		}

		public float CustomMin
		{
			get { return customMin; }
		}

		public float CustomMax
		{
			get { return customMax; }
		}

		public float CustomRange
		{
			get { return customRange; }
		}

		#endregion

		#region Big Map methods and fields

		/* MAP: Big Map height map caching */
		private float[,] big_heightmap;
		private bool cache;
		private double centeredLong, centeredLat;

		private void terrainHeightToArray(double lon, double lat, int ilon, int ilat)
		{
			float alt = 0f;
			alt = (float)SCANUtil.getElevation(body, lon, lat);
			if (alt == 0f)
			{
				alt = -0.001f;
			}

			big_heightmap[ilon, ilat] = alt;
		}

		/* MAP: Projection methods for converting planet coordinates to the rectangular texture */
		private MapProjection projection = MapProjection.Rectangular;

		internal void setProjection(MapProjection p)
		{
			if (projection == p)
			{
				return;
			}

			projection = p;
		}

		internal double projectLongitude(double lon, double lat)
		{
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.KavrayskiyVII:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					lon = (3.0f * lon / 2.0f / Math.PI) * Math.Sqrt(Math.PI * Math.PI / 3.0f - lat * lat);
					return Mathf.Rad2Deg * lon;
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					if (lat < 0)
					{
						lon = 1.3 * Math.Cos(lat) * Math.Sin(lon) - Math.PI / 2;
					}
					else
					{
						lon = 1.3 * Math.Cos(lat) * Math.Sin(lon) + Math.PI / 2;
					}
					return Mathf.Rad2Deg * lon;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					if (Math.Sin(centerLat) * Math.Sin(lat) + Math.Cos(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon) < 0)
					{
						return -200;
					}

					lon = 1.5 * Math.Cos(lat) * Math.Sin(lon - centerLon);

					return Mathf.Rad2Deg * lon;
				default:
					return lon;
			}
		}

		internal double projectLatitude(double lon, double lat)
		{
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					if (lat < 0)
					{
						lat = 1.3 * Math.Cos(lat) * Math.Cos(lon);
					}
					else
					{
						lat = -1.3 * Math.Cos(lat) * Math.Cos(lon);
					}
					return Mathf.Rad2Deg * lat;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					if (Math.Sin(centerLat) * Math.Sin(lat) + Math.Cos(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon) < 0)
					{
						return -200;
					}

					lat = 1.5 * (Math.Cos(centerLat) * Math.Sin(lat) - Math.Sin(centerLat) * Math.Cos(lat) * Math.Cos(lon - centerLon));

					return Mathf.Rad2Deg * lat;
				default:
					return lat;
			}
		}

		internal double unprojectLongitude(double lon, double lat)
		{
			if (lat > 90)
			{
				lat = 180 - lat;
				lon += 180;
			}
			else if (lat < -90)
			{
				lat = -180 - lat;
				lon += 180;
			}
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.KavrayskiyVII:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					lon = lon / Math.Sqrt(Mathf.PI * Math.PI / 3.0f - lat * lat) * 2.0f * Math.PI / 3.0f;
					return Mathf.Rad2Deg * lon;
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double lat0 = Math.PI / 2;
					if (lon < 0)
					{
						lon += Math.PI / 2;
						lat0 = -Math.PI / 2;
					}
					else
					{
						lon -= Math.PI / 2;
					}
					lon /= 1.3;
					lat /= 1.3;
					double p = Math.Sqrt(lon * lon + lat * lat);
					double c = Math.Asin(p);
					lon = Math.Atan2((lon * Math.Sin(c)), (p * Math.Cos(lat0) * Math.Cos(c) - lat * Math.Sin(lat0) * Math.Sin(c)));
					lon = (Mathf.Rad2Deg * lon + 180) % 360 - 180;
					if (lon <= -180)
					{
						lon = -180;
					}

					return lon;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLon = Mathf.Deg2Rad * centeredLong;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					double p2 = Math.Sqrt(lon * lon + lat * lat);
					double c2 = Math.Asin(p2 / 1.5);

					if (Math.Cos(c2) < 0)
					{
						return 300;
					}

					lon = centerLon + Math.Atan2(lon * Math.Sin(c2), p2 * Math.Cos(c2) * Math.Cos(centerLat) - lat * Math.Sin(c2) * Math.Sin(centerLat));

					lon = (Mathf.Rad2Deg * lon + 180) % 360 - 180;

					if (lon <= -180)
					{
						lon += 360;
					}

					return lon;
				default:
					return lon;
			}
		}

		internal double unprojectLatitude(double lon, double lat)
		{
			if (lat > 90)
			{
				lat = 180 - lat;
				lon += 180;
			}
			else if (lat < -90)
			{
				lat = -180 - lat;
				lon += 180;
			}
			lon = (lon + 3600 + 180) % 360 - 180;
			lat = (lat + 1800 + 90) % 180 - 90;
			switch (projection)
			{
				case MapProjection.Polar:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double lat0 = Math.PI / 2;
					if (lon < 0)
					{
						lon += Math.PI / 2;
						lat0 = -Math.PI / 2;
					}
					else
					{
						lon -= Math.PI / 2;
					}
					lon /= 1.3;
					lat /= 1.3;
					double p = Math.Sqrt(lon * lon + lat * lat);
					double c = Math.Asin(p);
					lat = Math.Asin(Math.Cos(c) * Math.Sin(lat0) + (lat * Math.Sin(c) * Math.Cos(lat0)) / (p));
					return Mathf.Rad2Deg * lat;
				case MapProjection.Orthographic:
					lon = Mathf.Deg2Rad * lon;
					lat = Mathf.Deg2Rad * lat;
					double centerLat = Mathf.Deg2Rad * centeredLat;

					double p2 = Math.Sqrt(lon * lon + lat * lat);
					double c2 = Math.Asin(p2 / 1.5);

					if (Math.Cos(c2) < 0)
					{
						return 300;
					}

					lat = Math.Asin(Math.Cos(c2) * Math.Sin(centerLat) + (lat * Math.Sin(c2) * Math.Cos(centerLat)) / p2);

					return Mathf.Rad2Deg * lat;
				default:
					return lat;
			}
		}

		/* MAP: scaling, centering (setting origin), translating, etc */
		private double mapscale, lon_offset, lat_offset;
		private int mapwidth, mapheight;
		private Color32[] pix;
		private bool resourceActive;
		private float[,] resourceCache;
		private int resourceInterpolation = 4;
		private int resourceMapWidth = 4;
		private int resourceMapHeight = 2;
		private double resourceMapScale = 1;
		private bool randomEdges = true;
		private double[] biomeIndex;
		private Color32[] stockBiomeColor;
		private int startLine;
		private int stopLine;
		double sunLonCenter;
		double sunLatCenter;
		double gamma;

		internal void setSize(Vector2 size)
		{
			setSize((int)size.x, (int)size.y);
		}

		internal void setSize(int w, int h, int interpolation = 2, int start = 0, int stop = 0)
		{
			if (w == 0)
			{
				w = 360 * (Screen.width / 360);
			}

			if (w > 360 * 4)
			{
				w = 360 * 4;
			}

			mapwidth = w;
			pix = new Color32[mapwidth];
			biomeIndex = new double[mapwidth];
			stockBiomeColor = new Color32[mapwidth];
			mapscale = mapwidth / 360f;
			if (h <= 0)
			{
				h = (int)(180 * mapscale);
			}

			mapheight = h;
			startLine = start;
			stopLine = stop == 0 ? mapheight - 1 : stop;
			resourceMapWidth = mapwidth;
			resourceMapHeight = mapheight;
			resourceCache = new float[resourceMapWidth, resourceMapHeight];
			resourceInterpolation = interpolation;
			resourceMapScale = resourceMapWidth / 360;
			randomEdges = false;
			if (map != null)
			{
				if (mapwidth != map.width || mapheight != map.height)
				{
					UnityEngine.Object.Destroy(map);
					map = null;
				}
			}
		}

		internal void setWidth(int w)
		{
			if (w == 0)
			{
				w = 360 * (int)(Screen.width / 360);
				if (w > 360 * 4)
				{
					w = 360 * 4;
				}
			}
			if (w < 360)
			{
				w = 360;
			}

			if (mapwidth == w)
			{
				return;
			}

			mapwidth = w;
			pix = new Color32[w];
			biomeIndex = new double[w];
			stockBiomeColor = new Color32[w];
			resourceMapHeight = SCAN_Settings_Config.Instance.ResourceMapHeight;
			resourceMapWidth = resourceMapHeight * 2;
			resourceInterpolation = SCAN_Settings_Config.Instance.Interpolation;
			resourceMapScale = resourceMapWidth / 360f;
			resourceCache = new float[resourceMapWidth, resourceMapHeight];
			randomEdges = true;
			mapscale = mapwidth / 360f;
			mapheight = (int)(w / 2);
			startLine = 0;
			stopLine = mapheight - 1;
			/* big map caching */
			big_heightmap = new float[mapwidth, mapheight];
			biome_indexmap = new float[mapwidth, mapheight];
			gpuDataBuf = new Color[mapwidth * mapheight];
			// Just wiped big_heightmap/biome_indexmap/gpuDataBuf. mapwidth is part of gpuConfigHash so
			// the stale claim can't match today, but the caches are empty either way - don't leave a
			// "data is complete" flag standing behind them.
			invalidateGpuDataCache();
			if (map != null)
				UnityEngine.Object.Destroy(map);
			map = null;
			resetMap(resourceActive);
		}

		// Free the Unity objects this map owns. Texture2D/RenderTexture/Material are NOT GC-managed
		// and are NOT auto-destroyed on scene load, so an orphaned SCANmap (the bigmap/spotmap
		// wrappers are re-created per scene) leaks them - SCANmap is a plain class with no finalizer.
		// The owning window's OnDestroy calls this. Safe to call more than once.
		internal void Destroy()
		{
			if (map != null) { UnityEngine.Object.Destroy(map); map = null; }
			if (coverageFlags != null) { UnityEngine.Object.Destroy(coverageFlags); coverageFlags = null; }
			if (compositeMaterial != null) { UnityEngine.Object.Destroy(compositeMaterial); compositeMaterial = null; }
			if (visualRenderTex != null) { visualRenderTex.Release(); UnityEngine.Object.Destroy(visualRenderTex); visualRenderTex = null; }
			if (exporter != null) { UnityEngine.Object.Destroy(exporter.gameObject); exporter = null; }
			if (elevationTex != null) { UnityEngine.Object.Destroy(elevationTex); elevationTex = null; }
			if (biomeIndexTex != null) { UnityEngine.Object.Destroy(biomeIndexTex); biomeIndexTex = null; }
			if (resourceTex != null) { UnityEngine.Object.Destroy(resourceTex); resourceTex = null; }
			if (paletteLUT != null) { UnityEngine.Object.Destroy(paletteLUT); paletteLUT = null; }
			if (paletteGreyLUT != null) { UnityEngine.Object.Destroy(paletteGreyLUT); paletteGreyLUT = null; }
			if (biomeLUT != null) { UnityEngine.Object.Destroy(biomeLUT); biomeLUT = null; }

			// The GPU data cache just went away with those textures, so its bookkeeping must go too.
			// bigmap/spotmap are STATIC: the SCANmap outlives this Destroy and gets reused next scene.
			// Leaving gpuDataComplete/gpuDataHash set means resetMap's instant-recolour path matches on
			// the next open with the same body+mode+projection+zoom, skips the whole sample sweep, and
			// re-Blits from biomeIndexTex/elevationTex that are now null - a blank map until you switch
			// body (which changes the hash) and back.
			invalidateGpuDataCache();
		}

		/// <summary>
		/// Drop every "the GPU already has valid data" claim. Anything that destroys or reallocates the
		/// data textures / CPU caches must call this, or resetMap will trust a cache that isn't there.
		/// </summary>
		private void invalidateGpuDataCache()
		{
			gpuDataComplete = false;
			gpuDataHash = 0;
			gpuRendered = false;
			gpuSweepDone = false;
			gpuRecolorSweep = false;
			resourceTexReady = false;
			resourceCacheReady = false;
			biomeLUTCount = 0;
			biomeLUTBody = null;
			paletteLUTHash = 0;
		}

		internal void centerAround(double lon, double lat)
		{
			centeredLong = lon;
			centeredLat = lat;

			if (projection == MapProjection.Orthographic)
			{
				double lo = projectLongitude(lon, lat);
				double la = projectLatitude(lon, lat);
				lon_offset = 180 + lo - (mapwidth / mapscale) / 2;
				lat_offset = 90 + la - (mapheight / mapscale) / 2;
			}
			else
			{
				lon_offset = 180 + lon - (mapwidth / mapscale) / 2;
				lat_offset = 90 + lat - (mapheight / mapscale) / 2;
			}
		}

		internal double scaleLatitude(double lat)
		{
			lat -= lat_offset;
			lat *= 180f / (mapheight / mapscale);
			return lat;
		}

		internal double scaleLongitude(double lon)
		{
			if (lon_offset < 0 && Math.Abs(lon_offset) < lon)
			{
				lon -= 360;
			}
			else if (lon_offset > 0 && Math.Abs(lon_offset) > lon)
			{
				lon += 360;
			}

			lon -= lon_offset;
			lon *= 360f / (mapwidth / mapscale);
			return lon;
		}

		private double unScaleLatitude(double lat)
		{
			lat -= lat_offset;
			lat += 90;
			lat *= mapscale;
			return lat;
		}

		private double unScaleLatitude(double lat, double scale)
		{
			lat -= lat_offset;
			lat += 90;
			lat *= scale;
			return lat;
		}

		private double unScaleLongitude(double lon)
		{
			lon -= lon_offset;
			lon += 180;
			lon *= mapscale;
			return lon;
		}

		private double unScaleLongitude(double lon, double scale)
		{
			lon -= lon_offset;
			lon = SCANUtil.fixLonShift(lon);
			lon += 180;
			lon *= scale;
			return lon;
		}

		private double fixUnscale(double value, int size)
		{
			if (value < 0)
			{
				value = 0;
			}
			else if (value >= (size - 0.5f))
			{
				value = size - 1;
			}

			return value;
		}

		/* MAP: internal state */
		private mapType mType;
		private mapSource mSource;
		private Texture2D map; // refs above: 214,215,216,232, below, and JSISCANsatRPM.
		private CelestialBody body = null; // all refs are below
		private SCANresourceGlobal resource;
		private SCANdata data;
		private SCANmapLegend mapLegend;
		private int mapstep; // all refs are below
		private int mapRedStep;
		private double[] mapline; // all refs are below
		private bool pqs;
		private bool biomeMap;
		private float customMin;
		private float customMax;
		private float customRange;
		private float customResourceMin;
		private float customResourceMax;
		private bool useCustomRange;
		private bool colorMap;
		private bool terminator;
		private float mapRedlineDraw = 10;

		/* GPU Visual-mode compositing: renders the Visual map on the GPU sampling the body's
		   original ScaledSpace textures, so no readable CPU copy is needed. Dormant until the
		   composite shader is present in the bundle; otherwise the CPU renderer below runs. */
		private RenderTexture visualRenderTex;
		private Material compositeMaterial;
		private Texture2D coverageFlags;
		private Color32[] coverageFlagsBuf;   // reused CPU buffer for coverageFlags (no per-frame alloc)
		private bool coverageFlagsDirty = true;   // set each resetMap; rebuild the coverage texture once per pass, not per sweep row
		private bool gpuRendered;
		private bool visualFallbackLogged;   // one "no GPU Visual source" log per map pass
			// GPU data textures for the non-Visual modes (all-modes port). Elevation/biome/resource
			// upload from the CPU caches (big_heightmap / biome_indexmap / resourceCache); a 1-D palette
			// LUT baked from heightToColor colourizes altimetry (legend parity). Shader branches _MapMode.
			private Texture2D elevationTex;
			private Texture2D biomeIndexTex;
			private Texture2D resourceTex;
			private Texture2D paletteLUT;
			private Texture2D paletteGreyLUT;   // LoRes-only altimetry grey ramp
			private Texture2D biomeLUT;         // stock biome mapColors
			private int biomeLUTCount;
			private CelestialBody biomeLUTBody;
			private float[,] biome_indexmap;
			private Color[] gpuDataBuf;
			private Color[] gpuRowBuf;
			private bool resourceTexReady;
			private bool resourceCacheReady;   // resourceCache is built this reset (by the prep loop or the lazy GPU build)
			private int paletteLUTHash;
			private bool gpuDataComplete;   // the non-Visual data cache is fully sampled for gpuDataHash's config
			private int gpuDataHash;        // config (body/mode/projection/size/offsets) the cached data is valid for
			private bool gpuRecolorSweep;   // cosmetic re-sweep in progress: re-Blit cached data with a new LUT (no re-sample)
		// The GPU compositor draws the whole Visual map in one Blit; these drive a purely
		// cosmetic scanline reveal (in the CPU path's row order) so it matches the CPU modes' look.
		// sweepStep advances one row per getPartialMap call (the pump calls it MapGenerationSpeed
		// times per frame, exactly like the CPU one-line-per-call cadence); gpuSweepDone gates
		// isMapComplete so the pump keeps re-compositing until the reveal finishes.
		private bool gpuSweepDone;
		private int sweepStep;

		/* MAP: nearly trivial functions */
		public void setBody(CelestialBody b)
		{
			SCANcontroller.controller.unloadPQS(body, mSource);
			SCANcontroller.controller.unloadOnDemandScaledSpace(body, mSource);

			if (body != b)
			{
				SCANcontroller.controller.UnloadVisualMapTexture(body, mSource);
				body = b;
				SCANcontroller.controller.loadOnDemandScaledSpace(body, mSource);
			}
			else
			{
				SCANcontroller.controller.loadOnDemandScaledSpace(body, mSource);
			}

			// The readable ScaledSpace copy is only consumed by the Visual map mode and is
			// very large under RSS (4K-8K bodies); load it lazily and only for that mode.
			refreshVisualMapTexture();

			data = SCANUtil.getData(body);

			SCANcontroller.controller.loadPQS(body, mSource);
			pqs = body.pqsController != null;
			biomeMap = body.BiomeMap != null;

			/* clear cache in place if necessary */
			if (cache)
			{
				for (int x = 0; x < mapwidth; x++)
				{
					for (int y = 0; y < mapwidth / 2; y++)
					{
						big_heightmap[x, y] = 0f;
					}
				}
			}

			if (SCANconfigLoader.GlobalResource)
			{
				if (resource != null)
				{
					resource.CurrentBodyConfig(body.bodyName);
				}
			}
		}

		/// <summary>
		/// Visual mode's texture sources are per-body (SCANcontroller.mapTextureHandler): the
		/// SCANSAT_BODY_TEXTURES paths and the GPU textures loaded from them. Register the body while
		/// this map is in Visual mode and release it otherwise, so nothing is held for maps that never
		/// show Visual.
		/// </summary>
		private void refreshVisualMapTexture()
		{
			if (SCANcontroller.controller == null || body == null)
			{
				return;
			}

			if (mType == mapType.Visual)
			{
				SCANcontroller.controller.LoadVisualMapTexture_Renamed(body, mSource);
			}
			else
			{
				SCANcontroller.controller.UnloadVisualMapTexture(body, mSource);
			}
		}

		public void setCustomRange(float min, float max, float rMin, float rMax)
		{
			useCustomRange = true;
			customMin = min;
			customMax = max;
			customRange = max - min;
			customResourceMin = rMin;
			customResourceMax = rMax;
		}

		internal bool isMapComplete()
		{
			if (gpuRendered)
			{
				// The map is composited, but hold "incomplete" until the cosmetic sweep finishes
				// so the update pump keeps re-Blitting the advancing scanline (like the CPU path).
				return gpuSweepDone;
			}

			if (map == null)
			{
				return false;
			}

			return mapstep >= map.height;
		}

		public void resetMap(bool resourceOn, bool setRes = true)
		{
			mapstep = -2;
			gpuRendered = false;
			gpuSweepDone = false;
			sweepStep = 0;
			resourceTexReady = false;
			resourceCacheReady = false;
			coverageFlagsDirty = true;   // new pass: refresh the GPU coverage stencil once from live coverage
			visualFallbackLogged = false;
			resourceActive = resourceOn;
			if (SCANconfigLoader.GlobalResource && setRes)
			{ //Make sure that a resource is initialized if necessary
				if (resource != null && body != null)
				{
					resource.CurrentBodyConfig(body.bodyName);
				}

				resetResourceMap();
			}

			switch (mSource)
			{
				case mapSource.BigMap:
					switch (SCAN_Settings_Config.Instance.MapGenerationSpeed)
					{
						case 1:
							mapRedlineDraw = 6;
							break;
						case 2:
							mapRedlineDraw = 3;
							break;
						case 3:
							mapRedlineDraw = 2;
							break;
					}
					break;
				case mapSource.ZoomMap:
					switch (SCAN_Settings_Config.Instance.MapGenerationSpeed)
					{
						case 1:
							mapRedlineDraw = 6;
							break;
						case 2:
							mapRedlineDraw = 3;
							break;
						case 3:
							mapRedlineDraw = 2;
							break;
					}
					break;
				case mapSource.RPM:
					mapRedlineDraw = 10;
					break;
			}

			if (terminator)
			{
				double sunLon = body.GetLongitude(Planetarium.fetch.Sun.position, false);
				double sunLat = body.GetLatitude(Planetarium.fetch.Sun.position, false);

				sunLatCenter = SCANUtil.fixLatShift(sunLat);

				if (sunLatCenter >= 0)
				{
					sunLonCenter = SCANUtil.fixLonShift(sunLon + 90);
				}
				else
				{
					sunLonCenter = SCANUtil.fixLonShift(sunLon - 90);
				}

				gamma = Math.Abs(sunLatCenter) < 0.55 ? 100 : Math.Tan(Mathf.Deg2Rad * (90 - Math.Abs(sunLatCenter)));
			}

			// mType is set (via MType or the resetMap(mode,...) overload) before every call
			// here, so this covers map-type switches that don't go through setBody.
			refreshVisualMapTexture();

			// Instant re-colour: if the GPU data cache is already fully sampled for this exact config
			// (a colourisation-only change - palette/clamp/terminator - leaves the config hash the same),
			// skip the re-sweep. Jump to the last row so the next getPartialMap re-Blits once with the
			// rebuilt LUT over the cached data textures instead of re-sampling PQS across the whole map.
			if (gpuDataComplete && gpuDataHash == gpuConfigHash() && willRenderGPU(mType)
				&& !(resourceActive && SCANconfigLoader.GlobalResource && resource != null)   // resource maps need the prep to rebuild resourceCache (resetResourceMap clears it)
				&& (mType == mapType.Altimetry || mType == mapType.Slope || mType == mapType.Biome))
			{
				mapstep = 0;               // replay the sweep from the top...
				gpuRendered = true;
				gpuSweepDone = false;
				gpuRecolorSweep = true;    // ...re-Blitting the cached data with the rebuilt LUT (charm, no re-sample)
				resourceTexReady = false;  // resource colours may have changed too
			}
		}

		public void resetMap(mapType mode, bool Cache, bool resourceOn, bool setRes = true)
		{
			mType = mode;
			cache = Cache;
			resetMap(resourceOn, setRes);
		}

		public void resetResourceMap()
		{
			if (mSource != mapSource.ZoomMap)
			{
				if (SCAN_Settings_Config.Instance.ResourceMapHeight != resourceMapHeight)
				{
					resourceMapHeight = SCAN_Settings_Config.Instance.ResourceMapHeight;
					resourceMapWidth = resourceMapHeight * 2;
					resourceMapScale = resourceMapWidth / 360f;
					resourceCache = new float[resourceMapWidth, resourceMapHeight];
				}

				if (SCAN_Settings_Config.Instance.Interpolation != resourceInterpolation)
				{
					resourceInterpolation = SCAN_Settings_Config.Instance.Interpolation;
				}
			}

			for (int i = 0; i < resourceMapWidth; i++)
			{
				for (int j = 0; j < resourceMapHeight; j++)
				{
					resourceCache[i, j] = 0;
				}
			}
		}

		/* MAP: export: PNG file */
		private SCANmapExporter exporter;

		internal void exportPNG()
		{
			if (exporter == null)
			{
				UnityEngine.GameObject obj = new GameObject();

				exporter = obj.gameObject.AddComponent<SCANmapExporter>();
			}

			if (exporter.Exporting)
			{
				return;
			}

			exporter.exportPNG(this, data);
		}

		#endregion

		#region Big Map Texture Generator

		// True when the GPU compositor is expected to render this map, so the readable CPU copy
		// can be skipped. Mirrors tryRenderVisualGPU's eligibility: Visual mode, resource overlay
		// off, composite shader present, and the body's ScaledSpace source textures ready.
		private bool willRenderGPU(mapType m)
		{
			if (SCAN_UI_Loader.VisualCompositeShader == null || body == null || data == null || SCANcontroller.controller == null)
				return false;
			switch (m)
			{
				case mapType.Visual: return SCAN_Settings_Config.Instance.VisibleMapsActive && SCANcontroller.controller.getScaledSpaceSource(body, out _, out _, out _, out _);   // the setting disables Visual maps outright (the CPU path only honoured it by accident)
				// Altimetry/Slope/Biome need the filled CPU caches (big_heightmap / biome_indexmap),
				// which only the cache=true map (BigMap, via setWidth) allocates + fills. ZoomMap/RPM
				// (cache=false, setSize) keep the CPU path for these modes; Visual GPU still works there.
				case mapType.Altimetry:
				case mapType.Slope: return pqs && cache;
				case mapType.Biome: return biomeMap && cache;
				default: return false;
			}
		}

		// Renders the Visual map on the GPU, sampling the body's ORIGINAL ScaledSpace textures so
		// no readable CPU copy is needed (that copy is the RSS RAM hog). Returns false (CPU
		// fallback) when not eligible - see willRenderGPU.
		private bool tryRenderGPU()
		{
			if (!willRenderGPU(mType))
				return false;

			Shader shader = SCAN_UI_Loader.VisualCompositeShader;

			// (source material / useMaterial flag are for a later gas-giant/Parallax pass)
			SCANcontroller.controller.getScaledSpaceSource(body, out Texture colorTex, out Texture normalTex, out _, out _);

			if (compositeMaterial == null || compositeMaterial.shader != shader)
				compositeMaterial = new Material(shader);

			if (visualRenderTex == null || visualRenderTex.width != mapwidth || visualRenderTex.height != mapheight)
			{
				if (visualRenderTex != null)
				{
					// Release() frees the GPU surface but not the RT object; Destroy the wrapper too
					// or the old RenderTexture leaks until finalization on every resize.
					visualRenderTex.Release();
					UnityEngine.Object.Destroy(visualRenderTex);
				}

				visualRenderTex = new RenderTexture(mapwidth, mapheight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				visualRenderTex.wrapMode = TextureWrapMode.Clamp;
			}

			updateCoverageFlags();

			compositeMaterial.SetTexture("_ScaledColor", colorTex);
			compositeMaterial.SetTexture("_ScaledNormal", normalTex);
			compositeMaterial.SetTexture("_CoverageFlags", coverageFlags);

			compositeMaterial.SetFloat("_MapWidth", mapwidth);
			compositeMaterial.SetFloat("_MapHeight", mapheight);
			compositeMaterial.SetFloat("_MapScale", (float)mapscale);
			compositeMaterial.SetFloat("_LonOffset", (float)lon_offset);
			compositeMaterial.SetFloat("_LatOffset", (float)lat_offset);
			compositeMaterial.SetFloat("_Projection", (float)(int)projection);
			compositeMaterial.SetFloat("_CenteredLon", (float)centeredLong);
			compositeMaterial.SetFloat("_CenteredLat", (float)centeredLat);
			compositeMaterial.SetFloat("_FlipY", 0f);
			compositeMaterial.SetFloat("_ColorMode", colorMap ? 1f : 0f);
			compositeMaterial.SetFloat("_HasNormal", normalTex != null ? 1f : 0f);
			compositeMaterial.SetFloat("_Terminator", terminator ? 1f : 0f);
			compositeMaterial.SetFloat("_SunLonCenter", (float)sunLonCenter);
			compositeMaterial.SetFloat("_SunLatCenter", (float)sunLatCenter);
			compositeMaterial.SetFloat("_Gamma", (float)gamma);

			Color unscanned = SCAN_Settings_Config.Instance.UnscannedColor;
			unscanned.a *= SCAN_Settings_Config.Instance.UnscannedTransparency;
			compositeMaterial.SetColor("_UnscannedColor", unscanned);
			compositeMaterial.SetColor("_ClearColor", palette.Clear);
				Color greyCol = palette.Grey; greyCol.a = 1f;
				compositeMaterial.SetColor("_GreyColor", greyCol);

				// Mode select + the non-Visual data textures / LUTs / overlay uniforms.
				compositeMaterial.SetFloat("_MapMode", (float)(int)mType);
				setModeUniforms();

			// Advance the cosmetic scanline one row per call, matching the CPU path's one-line-
			// per-call cadence (mapstep++). We re-composite with the reveal fraction each call so
			// the RawImage - already pointed at visualRenderTex - animates in place. Same background
			// colour the CPU path clears to (SCANmap.cs getPartialMap map-init), redline = palette.Red.
			if (mType == mapType.Visual && !gpuSweepDone)
			{
				sweepStep++;
				if (sweepStep >= mapheight)
					gpuSweepDone = true;
			}

			Color background = SCAN_Settings_Config.Instance.MapBackgroundColor;
			background.a *= SCAN_Settings_Config.Instance.BackgroundTransparency;
			compositeMaterial.SetColor("_MapBackgroundColor", background);
			compositeMaterial.SetColor("_RedlineColor", palette.Red);
			float gpuSweepRow = mType == mapType.Visual ? sweepStep : mapstep + 1;
				compositeMaterial.SetFloat("_SweepY", mapheight > 0 ? Mathf.Clamp01(gpuSweepRow / mapheight) : 1f);

			Graphics.Blit(null, visualRenderTex, compositeMaterial);

			gpuRendered = true;                       // DisplayTexture returns the RT during the sweep
			if (mType == mapType.Visual && gpuSweepDone)
				mapstep = mapheight;                  // mark complete for the legacy mapstep-based checks
			return true;
		}

		// Uploads the coverage bitmask as a 360x180 texture the composite shader samples as a
		// per-pixel stencil: R=VisualHiRes, G=VisualLoRes, B=ResourceHiRes, A=ResourceLoRes.
		// GPU mode data helpers:

		// Create the GPU RenderTexture (cleared to background) + set gpuRendered so DisplayTexture
		// returns it immediately - before tryRenderGPU has real data. Fixes the updateMap timing for
		// non-Visual modes: their tryRenderGPU runs in the getPartialMap branch (after the prep loop),
		// which is AFTER the BigMap pump consumes updateMap on the mode-switch frame, so without this
		// the RawImage stays pointed at the never-painted CPU map texture and shows blank. (Visual
		// primes via its own tryRenderGPU at the top of getPartialMap.)
		private void primeGpuRenderTex()
		{
			if (visualRenderTex == null || visualRenderTex.width != mapwidth || visualRenderTex.height != mapheight)
			{
				if (visualRenderTex != null) { visualRenderTex.Release(); UnityEngine.Object.Destroy(visualRenderTex); }
				visualRenderTex = new RenderTexture(mapwidth, mapheight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				visualRenderTex.wrapMode = TextureWrapMode.Clamp;
				RenderTexture prev = RenderTexture.active;
				RenderTexture.active = visualRenderTex;
				Color bg = SCAN_Settings_Config.Instance.MapBackgroundColor;
				bg.a *= SCAN_Settings_Config.Instance.BackgroundTransparency;
				GL.Clear(false, true, bg);
				RenderTexture.active = prev;
			}
			gpuRendered = true;
		}

		// Per-mode data textures + uniforms for tryRenderGPU. Visual's ScaledSpace textures are set by
		// the caller; here we upload the CPU-cache data for Altimetry/Slope/Biome + the resource overlay.
		private void setModeUniforms()
		{
			if (mType == mapType.Altimetry || mType == mapType.Slope)
			{
				compositeMaterial.SetTexture("_ElevationTex", elevationTex);   // uploaded incrementally per row in getPartialMap

				float tMin, tRange;
				if (useCustomRange) { tMin = customMin; tRange = customRange; }
				else { SCANterrainConfig tc = SCANUtil.getTerrainConfig(data); tMin = tc.MinTerrain; tRange = tc.MaxTerrain - tc.MinTerrain; }
				if (tRange <= 0f) tRange = 1f;
				compositeMaterial.SetFloat("_TerrainMin", tMin);
				compositeMaterial.SetFloat("_TerrainRange", tRange);

				if (mType == mapType.Altimetry)
				{
					buildPaletteLUT(tMin, tRange);
					compositeMaterial.SetTexture("_PaletteLUT", paletteLUT);
					compositeMaterial.SetTexture("_PaletteGreyLUT", paletteGreyLUT);
				}
				else
				{
					compositeMaterial.SetFloat("_SlopeCutoff", SCAN_Settings_Config.Instance.SlopeCutoff);
					compositeMaterial.SetColor("_SlopeLoColorOne", SCANcontroller.controller.lowSlopeColorOne32);
					compositeMaterial.SetColor("_SlopeHiColorOne", SCANcontroller.controller.highSlopeColorOne32);
					compositeMaterial.SetColor("_SlopeLoColorTwo", SCANcontroller.controller.lowSlopeColorTwo32);
					compositeMaterial.SetColor("_SlopeHiColorTwo", SCANcontroller.controller.highSlopeColorTwo32);
				}
			}
			else if (mType == mapType.Biome)
			{
				compositeMaterial.SetTexture("_BiomeIndexTex", biomeIndexTex);   // uploaded incrementally per row in getPartialMap
				compositeMaterial.SetColor("_LowBiomeColor", SCANcontroller.controller.lowBiomeColor32);
				compositeMaterial.SetColor("_HighBiomeColor", SCANcontroller.controller.highBiomeColor32);
				compositeMaterial.SetFloat("_BiomeTransparency", SCAN_Settings_Config.Instance.BiomeTransparency);
				bool border = mSource == mapSource.BigMap ? SCAN_Settings_Config.Instance.BigMapBiomeBorder : SCAN_Settings_Config.Instance.ZoomMapBiomeBorder;
				compositeMaterial.SetFloat("_BiomeBorder", border ? 1f : 0f);
				buildBiomeLUT();
				compositeMaterial.SetTexture("_BiomeLUT", biomeLUT);
				compositeMaterial.SetFloat("_BiomeCount", biomeLUTCount);
				compositeMaterial.SetFloat("_StockBiomes", (SCAN_Settings_Config.Instance.BigMapStockBiomes && colorMap) ? 1f : 0f);
				// elevation underlay: biome blends its colour with grey elevation by BiomeTransparency
				compositeMaterial.SetTexture("_ElevationTex", elevationTex);
				SCANterrainConfig tc = SCANUtil.getTerrainConfig(data);
				float bRange = tc.MaxTerrain - tc.MinTerrain;
				compositeMaterial.SetFloat("_TerrainMin", tc.MinTerrain);
				compositeMaterial.SetFloat("_TerrainRange", bRange <= 0f ? 1f : bRange);
			}

			bool resOn = resourceActive && SCANconfigLoader.GlobalResource && resource != null;
			compositeMaterial.SetFloat("_ResourceActive", resOn ? 1f : 0f);
			if (resOn)
			{
				if (!resourceCacheReady) { buildResourceCache(); resourceCacheReady = true; }   // GPU paths skip the prep that builds it
				if (!resourceTexReady) { uploadResourceTexture(); resourceTexReady = true; }
				compositeMaterial.SetTexture("_ResourceTex", resourceTex);
				float minR = useCustomRange ? customResourceMin : resource.CurrentBody.MinValue;
				float maxR = useCustomRange ? customResourceMax : resource.CurrentBody.MaxValue;
				compositeMaterial.SetColor("_ResMinColor", resource.MinColor32);
				compositeMaterial.SetColor("_ResMaxColor", resource.MaxColor32);
				compositeMaterial.SetFloat("_ResMinRange", minR);
				compositeMaterial.SetFloat("_ResMaxRange", maxR);
				compositeMaterial.SetFloat("_ResTransparency", resource.Transparency / 100f);
			}
		}

		// Config key (body/mode/projection/size/offsets). If it matches the value cached when the data
		// finished sampling, the change was colourisation-only (palette/clamp/terminator) and we can
		// instant-recolour from the cached data textures instead of re-sweeping the whole map.
		private int gpuConfigHash()
		{
			int h = body != null ? body.flightGlobalsIndex : -1;
			h = h * 31 + (int)mType;
			h = h * 31 + (int)projection;
			h = h * 31 + mapwidth;
			h = h * 31 + lon_offset.GetHashCode();
			h = h * 31 + lat_offset.GetHashCode();
			h = h * 31 + centeredLat.GetHashCode();
			h = h * 31 + centeredLong.GetHashCode();
			return h;
		}

		// Ensure the mode's R-float data texture exists (cleared to 0). Rows are then uploaded
		// incrementally by uploadDataRow as the CPU prep loop fills the cache, so we never rebuild
		// the whole texture per frame - only the newly-scanned row changes.
		private void ensureDataTex(ref Texture2D tex)
		{
			if (tex != null && tex.width == mapwidth && tex.height == mapheight) return;
			if (tex != null) UnityEngine.Object.Destroy(tex);
			tex = new Texture2D(mapwidth, mapheight, TextureFormat.RFloat, false);
			tex.wrapMode = TextureWrapMode.Clamp;
			if (gpuDataBuf == null || gpuDataBuf.Length != mapwidth * mapheight)
				gpuDataBuf = new Color[mapwidth * mapheight];
			System.Array.Clear(gpuDataBuf, 0, gpuDataBuf.Length);
			tex.SetPixels(gpuDataBuf);
			tex.Apply(false);
		}

		// Upload one geographic row (src column y=row) into the data texture.
		private void uploadDataRow(Texture2D tex, float[,] src, int row)
		{
			if (tex == null || src == null || row < 0 || row >= mapheight) return;
			if (gpuRowBuf == null || gpuRowBuf.Length != mapwidth)
				gpuRowBuf = new Color[mapwidth];
			for (int x = 0; x < mapwidth; x++)
				gpuRowBuf[x] = new Color(src[x, row], 0f, 0f, 0f);
			tex.SetPixels(0, row, mapwidth, 1, gpuRowBuf);
			tex.Apply(false);
		}

		// Upload resourceCache (geographic resW x resH) as an R-float abundance texture (fraction 0..1).
		// Build resourceCache (stock abundance) - the GPU paths (Visual short-circuit, fake-sweep fast
		// path) skip the prep loop that normally builds it, and resetResourceMap clears it every reset.
		private void buildResourceCache()
		{
			SCANuiUtil.generateResourceCache(ref resourceCache, resourceMapHeight, resourceMapWidth, resourceInterpolation, resourceMapScale, this);
			System.Random rr = new System.Random(ResourceScenario.Instance.gameSettings.Seed);
			for (int i = resourceInterpolation / 2; i >= 1; i /= 2)
			{
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, i, i, rr, randomEdges, mSource == mapSource.ZoomMap);
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, 0, i, i, rr, randomEdges, mSource == mapSource.ZoomMap);
				SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, 0, i, rr, randomEdges, mSource == mapSource.ZoomMap);
			}
		}

		private void uploadResourceTexture()
		{
			if (resourceCache == null) return;
			if (resourceTex == null || resourceTex.width != resourceMapWidth || resourceTex.height != resourceMapHeight)
			{
				if (resourceTex != null) UnityEngine.Object.Destroy(resourceTex);
				resourceTex = new Texture2D(resourceMapWidth, resourceMapHeight, TextureFormat.RFloat, false);
				resourceTex.wrapMode = TextureWrapMode.Clamp;
			}
			Color[] buf = new Color[resourceMapWidth * resourceMapHeight];
			for (int y = 0; y < resourceMapHeight; y++)
				for (int x = 0; x < resourceMapWidth; x++)
					buf[y * resourceMapWidth + x] = new Color(resourceCache[x, y] / 100f, 0f, 0f, 0f);
			resourceTex.SetPixels(buf);
			resourceTex.Apply(false);
		}

		// Bake heightToColor across [min, min+range] into a 1-D LUT so the shader is a plain fetch and
		// the map matches the legend (which calls the same heightToColor) by construction.
		// 1-D LUT of the body's stock biome mapColors, indexed by the biome fraction. Cached per body.
		private void buildBiomeLUT()
		{
			if (body.BiomeMap == null) return;
			int n = body.BiomeMap.Attributes.Length;
			if (biomeLUT != null && biomeLUTCount == n && biomeLUTBody == body) return;
			if (biomeLUT != null) UnityEngine.Object.Destroy(biomeLUT);
			int w = Mathf.Max(n, 1);
			biomeLUT = new Texture2D(w, 1, TextureFormat.RGBA32, false);
			biomeLUT.filterMode = FilterMode.Point;
			biomeLUT.wrapMode = TextureWrapMode.Clamp;
			Color[] c = new Color[w];
			for (int i = 0; i < n; i++) c[i] = body.BiomeMap.Attributes[i].mapColor;
			biomeLUT.SetPixels(c);
			biomeLUT.Apply(false);
			biomeLUTCount = n;
			biomeLUTBody = body;
		}

		private void buildPaletteLUT(float min, float range)
		{
			SCANterrainConfig tc = SCANUtil.getTerrainConfig(data);
			int hash = tc.ColorPal.Hash ^ (colorMap ? 1 : 0) ^ min.GetHashCode() ^ range.GetHashCode() ^ (useCustomRange ? 2 : 0);
			if (paletteLUT != null && paletteLUTHash == hash)
				return;
			if (paletteLUT == null)
			{
				paletteLUT = new Texture2D(1024, 1, TextureFormat.RGBA32, false);
				paletteLUT.wrapMode = TextureWrapMode.Clamp;
			}
			if (paletteGreyLUT == null)
			{
				paletteGreyLUT = new Texture2D(1024, 1, TextureFormat.RGBA32, false);
				paletteGreyLUT.wrapMode = TextureWrapMode.Clamp;
			}
			Color[] lut = new Color[1024];
			Color[] grey = new Color[1024];
			for (int x = 0; x < 1024; x++)
			{
				float val = min + (x / 1023f) * range;
				lut[x] = useCustomRange
					? (Color)palette.heightToColor(val, colorMap, tc, customMin, customMax, customRange, true)
					: (Color)palette.heightToColor(val, colorMap, tc);
				grey[x] = useCustomRange
					? (Color)palette.heightToColor(val, false, tc, customMin, customMax, customRange, true)
					: (Color)palette.heightToColor(val, false, tc);
			}
			paletteLUT.SetPixels(lut);
			paletteLUT.Apply(false);
			paletteGreyLUT.SetPixels(grey);
			paletteGreyLUT.Apply(false);
			paletteLUTHash = hash;
		}

		private void updateCoverageFlags()
		{
			bool created = false;
			if (coverageFlags == null)
			{
				coverageFlags = new Texture2D(360, 180, TextureFormat.RGBA32, false);
				coverageFlags.filterMode = FilterMode.Point;
				coverageFlags.wrapMode = TextureWrapMode.Clamp;
				created = true;
			}

			// Coverage only changes between render passes (resetMap sets coverageFlagsDirty), never
			// within the per-row sweep - so rebuild + GPU-upload once per pass instead of on every
			// tryRenderGPU call. The CPU buffer is a reused member, so the sweep allocates nothing.
			if (!created && !coverageFlagsDirty)
				return;

			if (coverageFlagsBuf == null)
				coverageFlagsBuf = new Color32[360 * 180];

			Int16[,] cov = data.Coverage;

			for (int x = 0; x < 360; x++)
			{
				for (int y = 0; y < 180; y++)
				{
					// Pack the raw 16-bit coverage: R = low byte, G = high byte. The shader decodes
					// cov = round(R*255) + 256*round(G*255) and tests any SCANtype bit (covHas). This
					// exposes every bit (Altimetry 0/1, VisualLoRes 2, Biome 3, VisualHiRes 6,
					// Resource 7/8) with one texture, vs the old 4-bit RGBA pack.
					int c = cov[x, y];
					coverageFlagsBuf[y * 360 + x] = new Color32((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), 0, 255);
				}
			}

			coverageFlags.SetPixels32(coverageFlagsBuf);
			coverageFlags.Apply(false);
			coverageFlagsDirty = false;
		}

		/* MAP: build: map to Texture2D */
		internal Texture2D getPartialMap(bool apply = true)
		{
			if (data == null)
			{
				return new Texture2D(1, 1);
			}

			if (mType == mapType.Visual && tryRenderGPU())
			{
				return map;
			}

			// Non-Visual GPU modes: point DisplayTexture at the RenderTexture up-front so the RawImage
			// tracks the GPU output on the same frame the UI consumes updateMap (the real data render
			// happens in the non-Visual branch below, after the prep loop fills the caches).
			if (mType != mapType.Visual && !gpuRendered && willRenderGPU(mType))
				primeGpuRenderTex();

			// Cosmetic re-sweep after a colour-only change: the data textures are already uploaded, so
			// skip the whole prep/CPU loop and just re-Blit each frame with the rebuilt LUT while the
			// sweep reveal advances - keeps the sweep charm with no PQS re-sample and no re-processing.
			if (gpuRecolorSweep)
			{
				tryRenderGPU();
				mapstep++;
				if (mapstep >= mapheight)
				{
					gpuSweepDone = true;
					gpuRecolorSweep = false;
					gpuDataComplete = true;
					gpuDataHash = gpuConfigHash();
				}
				return map;
			}

			System.Random r = new System.Random(ResourceScenario.Instance.gameSettings.Seed);

			bool resourceOn = false;
			bool mapHidden = mapstep < startLine || mapstep > stopLine;

			Color unscanned = SCAN_Settings_Config.Instance.UnscannedColor;
			unscanned.a *= SCAN_Settings_Config.Instance.UnscannedTransparency;

			// GPU non-Visual modes composite into visualRenderTex and never colourise the CPU map,
			// so skip its ~4 MB Texture2D + the background fill; the sweep uses mapwidth/mapheight.
			bool gpuNonVisual = mType != mapType.Visual && willRenderGPU(mType);

			if (map == null)
			{
				if (!gpuNonVisual)
				{
					map = new Texture2D(mapwidth, mapheight, TextureFormat.ARGB32, false);
					pix = map.GetPixels32();
					Color background = SCAN_Settings_Config.Instance.MapBackgroundColor;
					background.a *= SCAN_Settings_Config.Instance.BackgroundTransparency;
					for (int i = 0; i < pix.Length; ++i)
					{
						pix[i] = background;
					}

					map.SetPixels32(pix);
					mapline = new double[mapwidth];
					pix = new Color32[mapwidth];
				}
			}
			else if (mapstep >= mapheight)
			{
				return map;
			}

			if (palette.redline == null || palette.redline.Length != mapwidth)
			{
				palette.redline = new Color32[mapwidth];
				for (int i = 0; i < palette.redline.Length; ++i)
				{
					palette.redline[i] = palette.Red;
				}
			}

			resourceOn = resourceActive && SCANconfigLoader.GlobalResource && resource != null;

			if (mapstep <= -2)
			{
				if (resourceOn)
				{
					SCANuiUtil.generateResourceCache(ref resourceCache, resourceMapHeight, resourceMapWidth, resourceInterpolation, resourceMapScale, this);
				}

				mapstep++;
				return map;
			}

			if (mapstep <= -1)
			{
				if (resourceOn)
				{
					for (int i = resourceInterpolation / 2; i >= 1; i /= 2)
					{
						SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, i, i, r, randomEdges, mSource == mapSource.ZoomMap);
						SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, 0, i, i, r, randomEdges, mSource == mapSource.ZoomMap);
						SCANuiUtil.interpolate(resourceCache, resourceMapHeight, resourceMapWidth, i, 0, i, r, randomEdges, mSource == mapSource.ZoomMap);
					}
				}
			}

			// The CPU path only needs biomeIndex when drawing biome borders (it colourises from
			// stockBiomeColor); the GPU path colourises stock biomes from _BiomeLUT[biomeIndex], so
			// it needs biomeIndex filled every pixel regardless of the border toggle. Without this
			// the GPU biome map reads index 0 everywhere and draws a single flat colour.
			bool gpuBiomeNeedsIndex = willRenderGPU(mapType.Biome);

			for (int i = 0; i < mapwidth; i++)
			{
				/* Introduce altimetry check here; Use unprojected lat/long coordinates
				 * All cached altimetry data stored in a single 2D array in rectangular format
				 * Pull altimetry data from cache after unprojection
				 */


				double cacheLat = ((mapstep + 1) * 1.0f / mapscale) - 90f + lat_offset;
				double lon = (i * 1.0f / mapscale) - 180f + lon_offset;

				if (mType != mapType.Visual)
				{
					if (body.pqsController != null && cache && mapstep + 1 < mapheight)
					{
						if (big_heightmap[i, mapstep + 1] == 0f)
						{
							if (SCANUtil.isCovered(lon, cacheLat, data, SCANtype.Altimetry))
							{
								terrainHeightToArray(lon, cacheLat, i, mapstep + 1);
							}
						}
					}
				}

				if (mapstep < 0)
				{
					continue;
				}

				if (mapHidden)
				{
					continue;
				}

				if (mType != mapType.Biome || !biomeMap)
				{
					continue;
				}

				double lat = (mapstep * 1.0f / mapscale) - 90f + lat_offset;
				double la = lat, lo = lon;
				lat = unprojectLatitude(lo, la);
				lon = unprojectLongitude(lo, la);

				if (double.IsNaN(lat) || double.IsNaN(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180)
				{
					stockBiomeColor[i] = palette.clear;
					biomeIndex[i] = 0;
					continue;
				}

				if (SCAN_Settings_Config.Instance.BigMapStockBiomes && colorMap)
				{
					stockBiomeColor[i] = SCANUtil.getBiome(body, lon, lat).mapColor;

					switch (mSource)
					{
						case mapSource.BigMap:
							if (SCAN_Settings_Config.Instance.BigMapBiomeBorder || gpuBiomeNeedsIndex)
							{
								biomeIndex[i] = SCANUtil.getBiomeIndexFraction(body, lon, lat);
							}

							break;
						case mapSource.ZoomMap:
						case mapSource.RPM:
							if (SCAN_Settings_Config.Instance.ZoomMapBiomeBorder || gpuBiomeNeedsIndex)
							{
								biomeIndex[i] = SCANUtil.getBiomeIndexFraction(body, lon, lat);
							}

							break;
					}
				}
				else
				{
					biomeIndex[i] = SCANUtil.getBiomeIndexFraction(body, lon, lat);
				}
			}

			if (mapstep <= -1)
			{
				mapstep++;
				return map;
			}

			// GPU render for the non-Visual modes: the prep loop above did the unavoidable PQS/biome
			// sampling into the CPU caches (big_heightmap / biomeIndex); skip the CPU colourize loop,
			// upload the data, and let the shader colourize. tryRenderGPU reads mapstep for the sweep;
			// we advance it and flag complete like the CPU path.
			if (mType != mapType.Visual && willRenderGPU(mType))
			{
				// Incrementally upload just the row(s) the prep loop above filled - not the whole
				// texture per frame. Altimetry/Slope fill big_heightmap[.,mapstep+1] (look-ahead; row 0
				// was filled at mapstep=-1); Biome fills biomeIndex[.] for the current row.
				if (mType == mapType.Altimetry || mType == mapType.Slope)
				{
					ensureDataTex(ref elevationTex);
					if (mapstep == 0) uploadDataRow(elevationTex, big_heightmap, 0);
					uploadDataRow(elevationTex, big_heightmap, mapstep + 1);
				}
				else if (mType == mapType.Biome)
				{
					for (int bi = 0; bi < mapwidth; bi++)
						biome_indexmap[bi, mapstep] = (float)biomeIndex[bi];
					ensureDataTex(ref biomeIndexTex);
					uploadDataRow(biomeIndexTex, biome_indexmap, mapstep);
					ensureDataTex(ref elevationTex);   // for the biome elevation underlay
					if (mapstep == 0) uploadDataRow(elevationTex, big_heightmap, 0);
					uploadDataRow(elevationTex, big_heightmap, mapstep + 1);
				}
				tryRenderGPU();
				mapstep++;
				if (mapstep >= mapheight)
				{
					gpuSweepDone = true;
					gpuDataComplete = true;          // data cache fully sampled...
					gpuDataHash = gpuConfigHash();    // ...for this config (enables instant-recolour)
				}
				return map;
			}

			for (int i = 0; i < map.width; i++)
			{
				if (mapHidden)
				{
					pix[i] = palette.Clear;
					continue;
				}

				Color32 baseColor = palette.Grey;
				pix[i] = baseColor;
				float projVal = 0f;
				bool nowColor = colorMap;
				double lat = (mapstep * 1.0f / mapscale) - 90f + lat_offset;
				double lon = (i * 1.0f / mapscale) - 180f + lon_offset;
				double la = lat, lo = lon;
				lat = unprojectLatitude(lo, la);
				lon = unprojectLongitude(lo, la);

				if (double.IsNaN(lat) || double.IsNaN(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180)
				{
					pix[i] = palette.Clear;
					continue;
				}

				switch (mType)
				{
					case mapType.Altimetry:
						{
							if (!pqs)
							{
								baseColor = palette.lerp(palette.Black, palette.White, UnityEngine.Random.value);
							}
							else if (SCANUtil.isCovered(lon, lat, data, SCANtype.Altimetry))
							{
								projVal = terrainElevation(lon, lat, mapwidth, mapheight, big_heightmap, cache, data, out nowColor);

								if (useCustomRange)
								{
									baseColor = palette.heightToColor(projVal, nowColor, SCANUtil.getTerrainConfig(data), customMin, customMax, customRange, true);
								}
								else
								{
									baseColor = palette.heightToColor(projVal, nowColor, SCANUtil.getTerrainConfig(data));
								}
							}
							else
							{
								baseColor = unscanned;
							}

							break;
						}
					case mapType.Slope:
						{
							if (!pqs)
							{
								baseColor = palette.lerp(palette.Black, palette.White, UnityEngine.Random.value);
							}
							else if (SCANUtil.isCovered(lon, lat, data, SCANtype.Altimetry))
							{
								projVal = terrainElevation(lon, lat, mapwidth, mapheight, big_heightmap, cache, data, out nowColor);
								if (mapstep >= 0)
								{
									// This doesn't actually calculate the slope per se, but it's faster
									// than asking for yet more elevation data. Please don't use this
									// code to operate nuclear power plants or rockets.
									double v1 = mapline[i];
									if (i > 0)
									{
										v1 = Math.Max(v1, mapline[i - 1]);
									}

									if (i < mapline.Length - 1 && mapstep > 0)
									{
										v1 = Math.Max(v1, mapline[i + 1]);
									}

									float v = Mathf.Clamp((float)Math.Abs(projVal - v1) / (1000f / (float)mapscale), 0, 2f);
									if (!colorMap)
									{
										baseColor = palette.lerp(palette.Black, palette.White, v / 2f);
									}
									else
									{
										if (v < SCAN_Settings_Config.Instance.SlopeCutoff)
										{
											baseColor = palette.lerp(SCANcontroller.controller.lowSlopeColorOne32, SCANcontroller.controller.highSlopeColorOne32, v / SCAN_Settings_Config.Instance.SlopeCutoff);
										}
										else
										{
											baseColor = palette.lerp(SCANcontroller.controller.lowSlopeColorTwo32, SCANcontroller.controller.highSlopeColorTwo32, (v - SCAN_Settings_Config.Instance.SlopeCutoff) / (2 - SCAN_Settings_Config.Instance.SlopeCutoff));
										}
									}
								}
								mapline[i] = projVal;
							}
							else
							{
								baseColor = unscanned;
							}

							break;
						}
					case mapType.Biome:
						{
							if (!biomeMap)
							{
								baseColor = palette.lerp(palette.Black, palette.White, UnityEngine.Random.value);
							}
							else if (SCANUtil.isCovered(lon, lat, data, SCANtype.Biome))
							{
								Color32 biome = palette.Grey;
								if (!colorMap)
								{
									if ((i > 0 && mapline[i - 1] != biomeIndex[i]) || (mapstep > 0 && mapline[i] != biomeIndex[i]))
									{
										biome = palette.White;
									}
									else
									{
										biome = palette.lerp(palette.Black, palette.White, (float)biomeIndex[i]);
									}
								}
								else
								{
									Color32 elevation = palette.Grey;
									if (SCAN_Settings_Config.Instance.BiomeTransparency > 0)
									{
										if (!pqs)
										{
											elevation = palette.Grey;
										}
										else if (SCANUtil.isCovered(lon, lat, data, SCANtype.Altimetry))
										{
											projVal = terrainElevation(lon, lat, mapwidth, mapheight, big_heightmap, cache, data, out nowColor);
											if (useCustomRange)
											{
												elevation = palette.lerp(palette.Black, palette.White, Mathf.Clamp(projVal + (-1f * customMin), 0, customRange) / customRange);
											}
											else
											{
												elevation = palette.lerp(palette.Black, palette.White, Mathf.Clamp(projVal + (-1f * SCANUtil.getTerrainConfig(data).MinTerrain), 0, SCANUtil.getTerrainConfig(data).TerrainRange) / SCANUtil.getTerrainConfig(data).TerrainRange);
											}
										}
									}

									bool border = false;

									switch (mSource)
									{
										case mapSource.BigMap:
											if (SCAN_Settings_Config.Instance.BigMapBiomeBorder)
											{
												border = true;
											}

											break;
										case mapSource.ZoomMap:
										case mapSource.RPM:
											if (SCAN_Settings_Config.Instance.ZoomMapBiomeBorder)
											{
												border = true;
											}

											break;
									}

									if (border && ((i > 0 && mapline[i - 1] != biomeIndex[i]) || (mapstep > 0 && mapline[i] != biomeIndex[i])))
									{
										biome = palette.White;
									}
									else if (SCAN_Settings_Config.Instance.BigMapStockBiomes)
									{
										biome = palette.lerp(stockBiomeColor[i], elevation, SCAN_Settings_Config.Instance.BiomeTransparency);
									}
									else
									{
										biome = palette.lerp(palette.lerp(SCANcontroller.controller.lowBiomeColor32, SCANcontroller.controller.highBiomeColor32, (float)biomeIndex[i]), elevation, SCAN_Settings_Config.Instance.BiomeTransparency);
									}
								}

								baseColor = biome;
								mapline[i] = biomeIndex[i];
							}
							else
							{
								baseColor = unscanned;
							}

							break;
						}
					case mapType.Visual:
						{
							// Visual is GPU-only (tryRenderGPU). Reaching the CPU loop in Visual mode means
							// there is no eligible source right now (see willRenderGPU): shader missing or
							// unsupported, or no texture for this body. Draw unscanned rather than guess,
							// and say so once per pass.
							if (!visualFallbackLogged)
							{
								visualFallbackLogged = true;
								SCANUtil.SCANlog("[{0}] Visual map has no GPU source (shader unavailable or no texture) - drawing unscanned", body.bodyName);
							}

							baseColor = unscanned;
							break;
						}
				}

				if (resourceOn)
				{
					float abundance = 0;
					switch (projection)
					{
						case MapProjection.Rectangular:
						case MapProjection.KavrayskiyVII:
						case MapProjection.Polar:
							abundance = getResoureCache(lon, lat);
							break;
						case MapProjection.Orthographic:
							abundance = resourceCache[Mathf.RoundToInt(i * (resourceMapWidth / mapwidth)), Mathf.RoundToInt(mapstep * (resourceMapWidth / mapwidth))];
							break;
					}
					if (useCustomRange)
					{
						baseColor = SCANuiUtil.resourceToColor32(baseColor, resource, customResourceMin, customResourceMax, abundance, data, lon, lat);
					}
					else
					{
						baseColor = SCANuiUtil.resourceToColor32(baseColor, resource, resource.CurrentBody.MinValue, resource.CurrentBody.MaxValue, abundance, data, lon, lat);
					}
				}

				if (terminator)
				{
					double crossingLat = Math.Atan(gamma * Math.Sin(Mathf.Deg2Rad * lon - Mathf.Deg2Rad * sunLonCenter));

					if (sunLatCenter >= 0)
					{
						if (lat < crossingLat * Mathf.Rad2Deg)
						{
							pix[i] = palette.lerp(baseColor, palette.Black, 0.5f);
						}
						else
						{
							pix[i] = baseColor;
						}
					}
					else
					{
						if (lat > crossingLat * Mathf.Rad2Deg)
						{
							pix[i] = palette.lerp(baseColor, palette.Black, 0.5f);
						}
						else
						{
							pix[i] = baseColor;
						}
					}
				}
				else
				{
					pix[i] = baseColor;
				}
			}

			if (mapstep >= 0)
			{
				map.SetPixels32(0, mapstep, map.width, 1, pix);
			}

			mapstep++;

			if (apply)
			{
				mapRedStep++;
			}

			if (mapRedStep % mapRedlineDraw == 0 || mapstep >= map.height)
			{
				mapRedStep = 0;

				if (mapstep < map.height - 1)
				{
					map.SetPixels32(0, mapstep, map.width, 1, palette.redline);
				}

				if (apply || mapstep >= map.height)
				{
					map.Apply();
				}
			}

			return map;
		}

		/* Calculates the terrain elevation based on scanning coverage; fetches data from elevation cache if possible */
		private float terrainElevation(double Lon, double Lat, int w, int h, float[,] heightMap, bool c, SCANdata Data, out bool NowColor, bool exporting = false)
		{
			float elevation = 0f;
			NowColor = colorMap;
			if (SCANUtil.isCovered(Lon, Lat, Data, SCANtype.AltimetryHiRes))
			{
				if (c)
				{
					double lon = fixUnscale(unScaleLongitude(Lon), w);
					double lat = fixUnscale(unScaleLatitude(Lat), h);

					int ilon = Mathf.RoundToInt((float)lon);
					int ilat = Mathf.RoundToInt((float)lat);

					if (ilon >= w)
					{
						ilon = w - 1;
					}

					if (ilat >= h)
					{
						ilat = h - 1;
					}

					elevation = heightMap[ilon, ilat];
					if (elevation == 0f && !exporting)
					{
						elevation = (float)SCANUtil.getElevation(body, Lon, Lat);
					}
				}
				else
				{
					elevation = (float)SCANUtil.getElevation(body, Lon, Lat);
				}
			}
			else
			{
				if (c)
				{
					double lon = fixUnscale(unScaleLongitude(Lon), w);
					double lat = fixUnscale(unScaleLatitude(Lat), h);

					int ilon = ((int)(lon * 5)) / 5;
					int ilat = ((int)(lat * 5)) / 5;

					if (ilon >= w)
					{
						ilon = w - 1;
					}

					if (ilat >= h)
					{
						ilat = h - 1;
					}

					elevation = heightMap[ilon, ilat];
					if (elevation == 0f && !exporting)
					{
						elevation = (float)SCANUtil.getElevation(body, ((int)(Lon * 5)) / 5, ((int)(Lat * 5)) / 5);
					}
				}
				else
				{
					elevation = (float)SCANUtil.getElevation(body, ((int)(Lon * 5)) / 5, ((int)(Lat * 5)) / 5);
				}

				NowColor = false;
			}

			return elevation;
		}

		public float terrainElevation(double Lon, double Lat, int W, int H, float[,] heightMap, SCANdata Data, bool export = false)
		{
			bool c = true;

			return terrainElevation(Lon, Lat, W, H, heightMap, true, Data, out c, export);
		}

		private float getResoureCache(double Lon, double Lat)
		{
			double resourceLat = fixUnscale(unScaleLatitude(Lat, resourceMapScale), resourceMapHeight);
			double resourceLon = fixUnscale(unScaleLongitude(Lon, resourceMapScale), resourceMapWidth);

			int ilon = Mathf.RoundToInt((float)resourceLon);
			int ilat = Mathf.RoundToInt((float)resourceLat);

			if (ilon >= resourceMapWidth)
			{
				ilon = resourceMapWidth - 1;
			}

			if (ilat >= resourceMapHeight)
			{
				ilat = resourceMapHeight - 1;
			}

			return resourceCache[ilon, ilat];
		}

		#endregion

	}
}
