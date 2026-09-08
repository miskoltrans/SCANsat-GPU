// SCANsat map compositing on the GPU (all modes).
//
// A full-screen Blit shader that reproduces SCANmap.getPartialMap on the GPU. Originally the
// Visual mode only (so SCANsat no longer needs a CPU-readable copy of each body's ScaledSpace
// textures - the RSS RAM hog); now branches on _MapMode to also draw Altimetry / Slope / Biome,
// plus the resource overlay. The point for the non-Visual modes is latency/UX (instant recolour
// on palette / clamp / setting changes, no per-frame CPU scanline loop), NOT RAM.
//
// Data flow: the C# side (SCANmap.tryRenderGPU) uploads the mode's already-computed CPU data as a
// texture - elevation (big_heightmap), biome index (biome_indexmap), resource abundance
// (resourceCache) - plus 1-D palette LUTs baked by SCANcolorUtil.heightToColor (so map + legend
// stay pixel-consistent). The shader does the coverage mask + LUT/colorize + overlay + terminator.
//
// Coverage stencil is now the raw 16-bit SCANdata.coverage, packed R=low byte / G=high byte.
//
// NOTE: rebuild the scan_shaders asset bundle (SCANsat -> Build All Bundles, Unity 2019.4.18f1)
// after any change here; until then SCANsat uses the CPU path. Data-texture ORIENTATION (the geo
// UV mappings below) is the main thing to verify in-game - if a mode is mirrored/flipped, adjust
// the geoUV / elevUV / resUV construction (same class of fix as Visual's `fLon = 1 - fLon`).
Shader "Hidden/SCANsat/VisualComposite"
{
	Properties
	{
		_ScaledColor ("Scaled Color", 2D) = "gray" {}
		_ScaledNormal ("Scaled Normal", 2D) = "bump" {}
		_NormalYChannel ("Normal Y channel (0 = blue, 1 = green)", Float) = 0
		_CoverageFlags ("Coverage Flags", 2D) = "black" {}
		_ElevationTex ("Elevation", 2D) = "black" {}
		_BiomeIndexTex ("Biome Index", 2D) = "black" {}
		_ResourceTex ("Resource Abundance", 2D) = "black" {}
		_PaletteLUT ("Palette LUT", 2D) = "white" {}
		// Cosmetic sweep reveal. Defaults render the whole map (SweepY >= 1) so a fresh
		// Material is fully revealed even if the C# side never sets these (bundle/DLL skew).
		_SweepY ("Sweep Reveal", Float) = 1
		_MapMode ("Map Mode", Float) = 3
		_MapBackgroundColor ("Map Background", Color) = (0,0,0,1)
		_RedlineColor ("Redline", Color) = (1,0,0,1)
	}
	SubShader
	{
		Cull Off ZWrite Off ZTest Always

		Pass
		{
			CGPROGRAM
			#pragma vertex vert_img
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _ScaledColor;
			sampler2D _ScaledNormal;
			sampler2D _CoverageFlags;   // 360x180, point-sampled. R=low byte, G=high byte of SCANdata.coverage Int16
			sampler2D _ElevationTex;    // geographic mapW x mapH, R = raw elevation (metres); from big_heightmap
			sampler2D _BiomeIndexTex;   // geographic mapW x mapH, R = biome index fraction [0,1]
			sampler2D _ResourceTex;     // geographic resW x resH, R = abundance fraction [0,1]; from resourceCache
			sampler2D _PaletteLUT;      // 1-D (Nx1) elevation colour ramp baked from heightToColor (colour)
			sampler2D _PaletteGreyLUT;  // 1-D grey ramp for LoRes-only altimetry (nowColor=false)
			sampler2D _BiomeLUT;        // 1-D (biomeCount x1) stock biome mapColors, indexed by biome fraction

			// Projection / map framing (mirror SCANmap fields)
			float _MapWidth;
			float _MapHeight;
			float _MapScale;
			float _LonOffset;
			float _LatOffset;
			float _Projection;      // 0 Rectangular, 1 KavrayskiyVII, 2 Polar, 3 Orthographic
			float _CenteredLon;
			float _CenteredLat;
			float _FlipY;           // 1 to flip the vertical axis (Blit Y-orientation safety toggle)

			float _MapMode;         // 0 Altimetry, 1 Slope, 2 Biome, 3 Visual (mapType enum)

			// Visual layer
			float _ColorMode;       // 1 colour, 0 grayscale
			float _HasNormal;       // 1 if a normal map is bound
			float _NormalYChannel;  // 1: Y in green (DXT5nm, BC5); 0: Y in blue (uncompressed RGB normals)

			// Altimetry / Slope
			float _TerrainMin;      // LUT domain min (metres)
			float _TerrainRange;    // LUT domain range (metres)
			float _SlopeCutoff;
			float4 _SlopeLoColorOne;
			float4 _SlopeHiColorOne;
			float4 _SlopeLoColorTwo;
			float4 _SlopeHiColorTwo;

			// Biome
			float4 _LowBiomeColor;
			float4 _HighBiomeColor;
			float _BiomeTransparency;
			float _BiomeBorder;     // 1 draw white biome borders
			float _StockBiomes;     // 1 use stock biome mapColors (_BiomeLUT), 0 low/high gradient
			float _BiomeCount;      // number of biomes (for the _BiomeLUT index)

			// Resource overlay
			float _ResourceActive;  // 1 apply resource overlay on top of the base colour
			float4 _ResMinColor;
			float4 _ResMaxColor;
			float _ResMinRange;     // 0..100
			float _ResMaxRange;     // 0..100
			float _ResTransparency; // 0..1 (config /100)

			// Terminator
			float _Terminator;      // 1 on
			float _SunLonCenter;
			float _SunLatCenter;
			float _Gamma;

			float4 _UnscannedColor;
			float4 _ClearColor;
			float4 _GreyColor;      // palette.Grey (for below-min-range resource)

			// Cosmetic sweep reveal (matches the CPU modes' line-by-line render look).
			float _SweepY;                 // revealed fraction in texture-row space (uv.y). >=1 = done, no redline.
			float4 _MapBackgroundColor;    // unrevealed rows
			float4 _RedlineColor;          // the advancing scanline colour

			static const float SCAN_PI = 3.14159265358979;
			static const float DEG2RAD = 0.0174532925199433;
			static const float RAD2DEG = 57.2957795130823;

			// SCANmap.unprojectLongitude/unprojectLatitude prologue + normalization.
			void normalizeRaw(inout float lon, inout float lat)
			{
				if (lat > 90.0) { lat = 180.0 - lat; lon += 180.0; }
				else if (lat < -90.0) { lat = -180.0 - lat; lon += 180.0; }
				lon = fmod(lon + 3600.0 + 180.0, 360.0) - 180.0;
				lat = fmod(lat + 1800.0 + 90.0, 180.0) - 90.0;
			}

			// Pixel raw coords -> geographic lon/lat (degrees). Returns false for out-of-disc pixels.
			bool unproject(float lonRaw, float latRaw, out float lon, out float lat)
			{
				normalizeRaw(lonRaw, latRaw);
				lon = lonRaw;
				lat = latRaw;

				if (_Projection < 0.5)               // Rectangular
				{
					// identity
				}
				else if (_Projection < 1.5)          // KavrayskiyVII (lat unchanged)
				{
					float lonr = DEG2RAD * lonRaw;
					float latr = DEG2RAD * latRaw;
					lon = RAD2DEG * (lonr / sqrt(SCAN_PI * SCAN_PI / 3.0 - latr * latr) * 2.0 * SCAN_PI / 3.0);
					lat = latRaw;
				}
				else if (_Projection < 2.5)          // Polar
				{
					float lonr = DEG2RAD * lonRaw;
					float latr = DEG2RAD * latRaw;
					float lat0 = SCAN_PI / 2.0;
					if (lonr < 0.0) { lonr += SCAN_PI / 2.0; lat0 = -SCAN_PI / 2.0; }
					else { lonr -= SCAN_PI / 2.0; }
					lonr /= 1.3;
					latr /= 1.3;
					float p = sqrt(lonr * lonr + latr * latr);
					float c = asin(p);
					float gl = atan2(lonr * sin(c), p * cos(lat0) * cos(c) - latr * sin(lat0) * sin(c));
					gl = fmod(RAD2DEG * gl + 180.0, 360.0) - 180.0;
					if (gl <= -180.0) gl = -180.0;
					lon = gl;
					lat = RAD2DEG * asin(cos(c) * sin(lat0) + (latr * sin(c) * cos(lat0)) / p);
				}
				else                                  // Orthographic
				{
					float lonr = DEG2RAD * lonRaw;
					float latr = DEG2RAD * latRaw;
					float centerLon = DEG2RAD * _CenteredLon;
					float centerLat = DEG2RAD * _CenteredLat;
					float p2 = sqrt(lonr * lonr + latr * latr);
					float c2 = asin(p2 / 1.5);
					if (cos(c2) < 0.0) return false;   // back hemisphere
					float gl = centerLon + atan2(lonr * sin(c2), p2 * cos(c2) * cos(centerLat) - latr * sin(c2) * sin(centerLat));
					gl = fmod(RAD2DEG * gl + 180.0, 360.0) - 180.0;
					if (gl <= -180.0) gl += 360.0;
					lon = gl;
					lat = RAD2DEG * asin(cos(c2) * sin(centerLat) + (latr * sin(c2) * cos(centerLat)) / p2);
				}

				if (isnan(lon) || isnan(lat)) return false;
				if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0) return false;
				return true;
			}

			// SCANcolorUtil.ConvertToGrayscale weights.
			float3 grayscale(float3 c)
			{
				float l = saturate(c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722);
				return float3(l, l, l);
			}

			float3 rgb2hsl(float3 c)
			{
				float mn = min(min(c.r, c.g), c.b);
				float mx = max(max(c.r, c.g), c.b);
				float l = (mn + mx) * 0.5;
				float h = 0.0, s = 0.0;
				if (mn != mx)
				{
					float d = mx - mn;
					s = l < 0.5 ? d / (mx + mn) : d / (2.0 - mx - mn);
					if (mx == c.r)      h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0);
					else if (mx == c.g) h = (c.b - c.r) / d + 2.0;
					else                h = (c.r - c.g) / d + 4.0;
					h /= 6.0;
				}
				return float3(h, s, l);
			}

			float hue2rgb(float p, float q, float t)
			{
				if (t < 0.0) t += 1.0;
				if (t > 1.0) t -= 1.0;
				if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
				if (t < 1.0 / 2.0) return q;
				if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
				return p;
			}

			float3 hsl2rgb(float3 hsl)
			{
				if (hsl.y == 0.0) return float3(hsl.z, hsl.z, hsl.z);
				float q = hsl.z < 0.5 ? hsl.z * (1.0 + hsl.y) : hsl.z + hsl.y - hsl.z * hsl.y;
				float p = 2.0 * hsl.z - q;
				return float3(hue2rgb(p, q, hsl.x + 1.0 / 3.0), hue2rgb(p, q, hsl.x), hue2rgb(p, q, hsl.x - 1.0 / 3.0));
			}

			// Which channel of the normal map holds Y depends on its format (DXT5nm and BC5 keep it in
			// green, uncompressed RGB in blue); SCANtextures decides and passes _NormalYChannel.
			float normalY(float4 n)
			{
				return _NormalYChannel > 0.5 ? n.g : n.b;
			}

			// Modulate lightness by the normal map's Y channel (the CPU renderer used to do this per pixel).
			float3 normalSoftLight(float3 rgb, float lumOver)
			{
				float3 hsl = rgb2hsl(rgb);
				float lum = hsl.z;
				float opacity = 0.8;
				if (lum > 0.5)
					lum = opacity * (1.0 - (1.0 - (2.0 * (lumOver - 0.5))) * (1.0 - lum)) + (1.0 - opacity) * lum;
				else
					lum = opacity * (2.0 * lumOver * lum) + (1.0 - opacity) * lum;
				return hsl2rgb(float3(hsl.x, hsl.y, lum));
			}

			// Decode the packed 16-bit coverage cell (R=low byte, G=high byte) and test a bit.
			float decodeCoverage(float2 geoStencilUV)
			{
				float2 f = tex2D(_CoverageFlags, geoStencilUV).rg;
				return floor(f.r * 255.0 + 0.5) + 256.0 * floor(f.g * 255.0 + 0.5);
			}
			bool covHas(float cov, float bit)  // bit = SCANtype exponent (AltLo=0,AltHi=1,VisLo=2,Biome=3,VisHi=6,ResLo=7,ResHi=8)
			{
				return fmod(floor(cov / exp2(bit)), 2.0) >= 0.5;
			}

			fixed4 frag(v2f_img i) : SV_Target
			{
				float vy = _FlipY > 0.5 ? 1.0 - i.uv.y : i.uv.y;

				// Pixel -> raw coord (SCANmap.cs:1023-1024), then unproject.
				float lonRaw = (i.uv.x * _MapWidth / _MapScale) - 180.0 + _LonOffset;
				float latRaw = (vy * _MapHeight / _MapScale) - 90.0 + _LatOffset;

				float lon, lat;
				if (!unproject(lonRaw, latRaw, lon, lat))
					return _ClearColor;

				// Coverage stencil lookup (SCANUtil.icLON/icLAT -> Coverage[ilon,ilat]).
				float ilon = fmod(floor(lon + 540.0), 360.0);
				float ilat = fmod(floor(lat + 270.0), 180.0);
				float cov = decodeCoverage(float2((ilon + 0.5) / 360.0, (ilat + 0.5) / 180.0));

				// Geographic UVs for the map-resolution data textures (equirectangular).
				// NOTE verify orientation in-game; mirror like Visual's fLon if a mode comes out flipped.
				float2 geoUV = float2(saturate((lon + 180.0) / 360.0), saturate((lat + 90.0) / 180.0));

				// Base ScaledSpace UV for Visual (SCANmap.cs:1183-1199).
				float fLat = saturate((lat + 90.0) / 180.0);
				float fLon = (lon + 270.0) / 360.0;
				if (fLon < 0.0) fLon += 1.0;
				if (fLon > 1.0) fLon -= 1.0;
				fLon = saturate(1.0 - fLon);

				float4 col = _UnscannedColor;

				if (_MapMode < 0.5)             // ---- Altimetry ----
				{
					if (covHas(cov, 0.0) || covHas(cov, 1.0))
					{
						float elev = tex2D(_ElevationTex, geoUV).r;
						float t = _TerrainRange > 0.0 ? saturate((elev - _TerrainMin) / _TerrainRange) : 0.5;
						// HiRes -> colour ramp; LoRes-only -> grey ramp (matches CPU nowColor).
						col = covHas(cov, 1.0) ? tex2D(_PaletteLUT, float2(t, 0.5)) : tex2D(_PaletteGreyLUT, float2(t, 0.5));
						col.a = 1.0;
					}
				}
				else if (_MapMode < 1.5)        // ---- Slope ----
				{
					if (covHas(cov, 0.0) || covHas(cov, 1.0))
					{
						// True gradient from neighbour elevation texels (cleaner than the CPU
						// cross-scanline max-diff; won't match it pixel-for-pixel by design).
						float2 tx = float2(1.0 / _MapWidth, 1.0 / _MapHeight);
						float e  = tex2D(_ElevationTex, geoUV).r;
						float eR = tex2D(_ElevationTex, geoUV + float2(tx.x, 0)).r;
						float eU = tex2D(_ElevationTex, geoUV + float2(0, tx.y)).r;
						float v = saturate(max(abs(e - eR), abs(e - eU)) / (1000.0 / _MapScale) * 0.5);
						v = min(v, 2.0);
						if (v < _SlopeCutoff)
							col = lerp(_SlopeLoColorOne, _SlopeHiColorOne, v / _SlopeCutoff);
						else
							col = lerp(_SlopeLoColorTwo, _SlopeHiColorTwo, (v - _SlopeCutoff) / (2.0 - _SlopeCutoff));
						col.a = 1.0;
					}
				}
				else if (_MapMode < 2.5)        // ---- Biome ----
				{
					if (covHas(cov, 3.0))
					{
						// biome_indexmap is filled per rendered pixel (unprojected) = map-pixel space, NOT the
					// geographic equirect grid the elevation cache uses - so sample it at the fragment uv.
					float2 bpix = float2(i.uv.x, vy);
					float bIdx = tex2D(_BiomeIndexTex, bpix).r;
						float2 tx = float2(1.0 / _MapWidth, 1.0 / _MapHeight);
						float bL = tex2D(_BiomeIndexTex, bpix - float2(tx.x, 0)).r;
						float bD = tex2D(_BiomeIndexTex, bpix - float2(0, tx.y)).r;
						if (_BiomeBorder > 0.5 && (abs(bIdx - bL) > 0.0001 || abs(bIdx - bD) > 0.0001))
						{
							col = float4(1.0, 1.0, 1.0, 1.0);   // palette.White border
						}
						else
						{
							// SCANsat low/high gradient (stock-biome LUT path handled CPU-side for now).
							// stock mapColor (LUT by biome fraction) or low/high gradient, + grey elevation underlay
						float4 g = _StockBiomes > 0.5 ? tex2D(_BiomeLUT, float2(bIdx + 0.5 / max(_BiomeCount, 1.0), 0.5)) : lerp(_LowBiomeColor, _HighBiomeColor, bIdx);
						float belev = tex2D(_ElevationTex, geoUV).r;
						float eg = _TerrainRange > 0.0 ? saturate((belev - _TerrainMin) / _TerrainRange) : 0.5;
							col = lerp(g, float4(eg, eg, eg, 1.0), _BiomeTransparency);
							col.a = 1.0;
						}
					}
				}
				else                            // ---- Visual (3) ----
				{
					bool visHi = covHas(cov, 6.0);
					bool visLo = covHas(cov, 2.0);
					if (visHi)
					{
						col = tex2D(_ScaledColor, float2(fLon, fLat));
						if (_ColorMode > 0.5)
						{
							if (_HasNormal > 0.5)
								col.rgb = normalSoftLight(col.rgb, normalY(tex2D(_ScaledNormal, float2(fLon, fLat))));
						}
						else
						{
							col.rgb = grayscale(col.rgb);
						}
						col.a = 1.0;
					}
					else if (visLo)
					{
						float2 q = float2(floor(fLon * 512.0) / 512.0, floor(fLat * 256.0) / 256.0);
						col = tex2D(_ScaledColor, q);
						if (_ColorMode <= 0.5)
							col.rgb = grayscale(col.rgb);
						col.a = 1.0;
					}
				}

				// Resource overlay on top of the base colour (SCANmap.cs:1496-1518, resourceToColor32).
				if (_ResourceActive > 0.5)
				{
					bool resHi = covHas(cov, 8.0);
					bool resLo = covHas(cov, 7.0);
					if (resHi || resLo)
					{
						float ab = tex2D(_ResourceTex, geoUV).r * 100.0;   // stored as fraction, *100 -> percent
						if (resLo && !resHi)
							ab = floor(ab / 5.0) * 5.0 + 2.5;              // LoRes 5% buckets
						if (ab < _ResMinRange)
							col = lerp(col, _GreyColor, _ResTransparency);
						else
						{
							float rt = _ResMaxRange > _ResMinRange ? (ab - _ResMinRange) / (_ResMaxRange - _ResMinRange) : 0.0;
							col = lerp(lerp(_ResMinColor, _ResMaxColor, saturate(rt)), col, _ResTransparency);
						}
					}
				}

				// Terminator day/night darkening (SCANmap.cs:1342-1360).
				if (_Terminator > 0.5)
				{
					float crossingLat = atan(_Gamma * sin(DEG2RAD * lon - DEG2RAD * _SunLonCenter)) * RAD2DEG;
					bool night = _SunLatCenter >= 0.0 ? (lat < crossingLat) : (lat > crossingLat);
					if (night)
						col.rgb = lerp(col.rgb, float3(0.0, 0.0, 0.0), 0.5);
				}

				// Cosmetic sweep reveal (unchanged): rows ahead of the scanline drawn as background,
				// the frontier as a redline. Matches the CPU modes' row order.
				if (_SweepY < 1.0)
				{
					float band = 2.0 / _MapHeight;
					if (i.uv.y > _SweepY)
						col = _MapBackgroundColor;
					else if (i.uv.y > _SweepY - band)
						col = _RedlineColor;
				}

				return col;
			}
			ENDCG
		}
	}
	Fallback Off
}
