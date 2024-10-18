using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using UnityEngine.Tilemaps;

namespace ProceduralMap
{
    public class TileMapGenerator : MonoBehaviour
    {
        [Header("Scene References")]
        public Tilemap tileMap;

        [Header("References")]
        public PolygonMap generator;
        public ComputeShader computeShader;

        [Header("Options")]
        [Tooltip("Resolution in Tiles")]
        public Vector2Int tileMapResolution = new Vector2Int(512, 512);
        [Tooltip("Tile size in pixels")]
        public int tileSizePixels = 16;
        public ViewBG background;
        public Overlays overlays;
        public BiomeColor[] biomes;

        private Color[,] texColors;
        private RenderTexture rt;
        private int buildTextureKernelIndex;
        private int findClosestCellKernelIndex;
        private int[] cellIDs;

        private const int POINT_SIZE = 5;

        private void Awake()
        {
            //Create a render texture and enable random write so we can rend things to it
            rt = new RenderTexture(tileMapResolution.x, tileMapResolution.y, 0, RenderTextureFormat.ARGB32);
            rt.enableRandomWrite = true;
            rt.filterMode = FilterMode.Point;
            rt.Create();

            //Get the kernel IDs
            buildTextureKernelIndex = computeShader.FindKernel("GenerateTexture");
            findClosestCellKernelIndex = computeShader.FindKernel("FindClosestCell");

            //Set the texture for the shader
            computeShader.SetTexture(buildTextureKernelIndex, "_Result", rt);
        }

        private void OnEnable()
        {
            if (generator != null)
            {
                generator.onMapGenerated += GenerateDebugTexture;
            }
        }

        private void OnDisable()
        {
            if (generator != null)
            {
                generator.onMapGenerated -= GenerateDebugTexture;
            }

            rt?.Release();
        }

        private void GenerateDebugTexture()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only Available in Play Mode");
                return;
            }

            if (tileMap == null || generator == null || generator.cells == null || generator.corners.Count == 0)
                return;

            texColors = new Color[tileMapResolution.x, tileMapResolution.y];

            //Populate the cellIDs array with the ID of the closest cell for each pixel
            cellIDs = GetClosestCenterForPixels();

            //Draw the debug info into the texture. The order here defines the draw order

            //BACKGROUND
            switch (background)
            {
                case ViewBG.VoronoiCells:
                    DrawVoronoiCells();
                    break;
                case ViewBG.Shape:
                    DrawShape();
                    break;
                case ViewBG.WaterAndLand:
                    DrawWaterAndLand();
                    break;
                case ViewBG.Islands:
                    DrawIslands();
                    break;
                case ViewBG.Elevation:
                    DrawElevation();
                    break;
                case ViewBG.Moisture:
                    DrawMoisture();
                    break;
                case ViewBG.Biomes:
                    DrawBiomes();
                    break;
                default:
                    break;
            }

            //OVERLAYS
            if ((overlays & Overlays.Borders) != 0)
                DrawMapBorders();

            if ((overlays & Overlays.VoronoiEdges) != 0) //Here we "create" a new byte value by comparing "mode" with "voronoiCells" using a "&" (AND) bitwise operator. As an example, the comparision works like this: 0011 & 0110 = 0010. Then we compare with "0", since zero is "0000".
                DrawVoronoiEdges();

            if ((overlays & Overlays.VoronoiCorners) != 0)
                DrawVoronoiCorners();

            if ((overlays & Overlays.DelaunayEdges) != 0)
                DrawDelaunayEdges();

            if ((overlays & Overlays.DelaunayCorners) != 0)
                DrawDelaunayCorners();

            if ((overlays & Overlays.Coast) != 0)
                DrawCoast();

            if ((overlays & Overlays.Rivers) != 0)
                DrawRivers();

            if ((overlays & Overlays.Slopes) != 0)
                DrawSlopes();

            //Create the texture and assign the texture using a Helper Compute Shader
            ApplyChangesToTexture();
        }

        private void ApplyChangesToTexture()
        {
            //Create a new Buffer
            ComputeBuffer shaderBuffer = new ComputeBuffer(tileMapResolution.x * tileMapResolution.y, sizeof(float) * 4 + sizeof(int) * 2); //4 because a color has 4 channels, 2 because the position has X and Y
            ColorData[] colorData = new ColorData[tileMapResolution.x * tileMapResolution.y];

            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    ColorData data = new ColorData()
                    {
                        position = new Vector2Int(x, y),
                        color = texColors[x, y]
                    };

                    colorData[x + y * tileMapResolution.y] = data;
                }
            }

            shaderBuffer.SetData(colorData);
            computeShader.SetBuffer(buildTextureKernelIndex, "_ColorData", shaderBuffer);
            computeShader.SetInt("_Resolution", tileMapResolution.x);
            computeShader.Dispatch(buildTextureKernelIndex, tileMapResolution.x / 8, tileMapResolution.y / 8, 1);
            shaderBuffer?.Dispose();
        }

        private Vector2Int MapGraphCoordToTextureCoords(float x, float y)
        {
            Vector2Int pos = new Vector2Int()
            {
                x = (int)(x / generator.size.x * tileMapResolution.x),
                y = (int)(y / generator.size.y * tileMapResolution.y)
            };

            return pos;
        }

        private Vector2 MapTextureCoordToGraphCoords(int x, int y)
        {
            Vector2 pos = new Vector2()
            {
                x = (x / (float)tileMapResolution.x) * generator.size.x,
                y = (y / (float)tileMapResolution.y) * generator.size.y
            };

            return pos;
        }

        private CellCenter GetClosestCenterFromPoint(Vector2 point)
        {
            float smallestDistance = float.MaxValue;
            CellCenter c = null;

            foreach (var center in generator.cells)
            {
                float d = Vector2.Distance(point, center.position);
                if (d < smallestDistance)
                {
                    smallestDistance = d;
                    c = center;
                }
            }

            return c;
        }

        private int[] GetClosestCenterForPixels()
        {
            //Send the data to the compute shader so the GPU can do the hard work
            CellData[] cellData = new CellData[generator.cells.Count];
            ComputeBuffer cellDataBuffer = new ComputeBuffer(generator.cells.Count, sizeof(int) * 2);
            ComputeBuffer cellIdByPixeldBuffer = new ComputeBuffer(tileMapResolution.x * tileMapResolution.y, sizeof(int)); //This is the buffer we're gonna read from

            for (int i = 0; i < cellData.Length; i++)
            {
                CellCenter c = generator.cells[i];
                cellData[i] = new CellData()
                {
                    position = MapGraphCoordToTextureCoords(c.position.x, c.position.y)
                };
            }

            cellDataBuffer.SetData(cellData);
            cellIdByPixeldBuffer.SetData(new int[tileMapResolution.x * tileMapResolution.y]); //we pass an empty array, since we just want to retrieve this data
            computeShader.SetBuffer(findClosestCellKernelIndex, "_CellData", cellDataBuffer);
            computeShader.SetBuffer(findClosestCellKernelIndex, "_CellIDByPixel", cellIdByPixeldBuffer);
            computeShader.SetInt("_Resolution", tileMapResolution.x);

            computeShader.Dispatch(findClosestCellKernelIndex, tileMapResolution.x / 8, tileMapResolution.y / 8, 1);

            //Get the result data back from the GPU
            int[] centersIDs = new int[tileMapResolution.x * tileMapResolution.y];
            cellIdByPixeldBuffer.GetData(centersIDs);

            cellDataBuffer?.Dispose();
            cellIdByPixeldBuffer?.Dispose();

            return centersIDs;
        }

        private void DrawVoronoiCells()
        {
            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * tileMapResolution.y];
                    float value = currentCellID / (float)generator.cells.Count;
                    texColors[x, y] = Color.HSVToRGB(1, 0, value);
                }
            }
        }

        private void DrawVoronoiEdges()
        {
            foreach (var edge in generator.edges)
            {
                DrawGraphEdge(edge, Color.white, 1, true);
            }
        }

        private void DrawVoronoiCorners()
        {
            foreach (var corner in generator.corners)
            {
                DrawGraphPoint(corner, Color.blue, POINT_SIZE);
            }
        }

        private void DrawDelaunayEdges()
        {
            foreach (var edge in generator.edges)
            {
                DrawGraphEdge(edge, Color.black, 1, false);
            }
        }

        private void DrawDelaunayCorners()
        {
            foreach (var center in generator.cells)
            {
                DrawGraphPoint(center, Color.red, POINT_SIZE);
            }
        }

        private void DrawMapBorders()
        {
            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    CellCenter c = generator.cells[cellIDs[x + y * tileMapResolution.y]];

                    if (c.isBorder)
                    {
                        texColors[x, y] = Color.red;
                    }
                }
            }

            Color borderCorner = new Color(1, .5f, .5f);

            foreach (var corner in generator.corners)
            {
                if (corner.isBorder)
                {
                    DrawGraphPoint(corner, borderCorner, POINT_SIZE * 2);
                }
            }
        }

        private void DrawShape()
        {
            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    texColors[x, y] = generator.shape.IsPointInsideShape(new Vector2(x, y), tileMapResolution, generator.seed) ? Color.gray : Color.black;
                }
            }
        }

        private void DrawWaterAndLand()
        {
            Color ocean = new Color(.1f, .1f, .5f);
            Color water = new Color(.5f, .5f, .7f);
            Color land = new Color(.7f, .7f, .5f);
            Color coast = new Color(.5f, .5f, .3f);

            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * tileMapResolution.y];
                    CellCenter c = generator.cells[currentCellID];

                    if (c.isWater)
                    {
                        if (c.isOcean)
                        {
                            texColors[x, y] = ocean;
                        }
                        else
                        {
                            texColors[x, y] = water;
                        }
                    }
                    else
                    {
                        if (c.isCoast)
                        {
                            texColors[x, y] = coast;
                        }
                        else
                        {
                            texColors[x, y] = land;
                        }
                    }
                }
            }
        }

        private void DrawIslands()
        {
            Dictionary<int, Color> islandColors = new Dictionary<int, Color>();

            for (int i = 0; i < generator.islands.Count; i++)
            {
                islandColors.Add(generator.islands[i][0].islandID, Color.HSVToRGB((i * (360f / generator.islands.Count)) / 360f, Random.Range(.5f, 1), 1));
            }

            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * tileMapResolution.y];
                    CellCenter c = generator.cells[currentCellID];

                    if (c.islandID < 0)
                    {
                        texColors[x, y] = Color.black;
                    }
                    else
                    {
                        texColors[x, y] = islandColors[c.islandID];
                    }
                }
            }
        }

        private void DrawCoast()
        {
            foreach (var corner in generator.corners)
            {
                if (corner.isCoast)
                {
                    DrawGraphPoint(corner, Color.white, POINT_SIZE, 2);
                }
            }
        }

        private void DrawElevation()
        {
            Color low = Color.black;
            Color high = Color.white;
            Color water = Color.blue * 0.5f;
            water.a = 1;
            Color waterDeep = Color.blue * 0.1f;
            waterDeep.a = 1;

            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    CellCenter c = generator.cells[cellIDs[x + y * tileMapResolution.y]];

                    if (c.elevation < 0)
                    {
                        texColors[x, y] = Color.Lerp(waterDeep, water, 1 + c.elevation);
                    }
                    else
                    {
                        texColors[x, y] = Color.Lerp(low, high, c.elevation);
                    }
                }
            }
        }

        private void DrawSlopes()
        {
            foreach (var corner in generator.corners)
            {
                //if (corner.isOcean || corner.isCoast)
                //{
                //	continue;
                //}

                if (corner.downslopeCorner == null)
                {
                    continue;
                }

                Vector2Int pos = MapGraphCoordToTextureCoords(corner.position.x, corner.position.y);
                Vector2Int pos2 = MapGraphCoordToTextureCoords(corner.downslopeCorner.position.x, corner.downslopeCorner.position.y);
                Vector2 dir = pos2 - pos;
                DrawArrow(pos.x, pos.y, dir, dir.magnitude * 0.4f, POINT_SIZE / 3, Color.red);
            }
        }

        private void DrawRivers()
        {
            foreach (var edge in generator.edges)
            {
                if (edge.waterVolume > 0)
                {
                    DrawGraphEdge(edge, Color.blue, edge.waterVolume * 3, true);
                }
            }
        }

        private void DrawMoisture()
        {
            Color water = new Color(.1f, .1f, .5f);
            Color wet = new Color(0.25f, .39f, .2f);
            Color dry = new Color(.8f, .7f, .5f);

            for (int x = 0; x < tileMapResolution.x; x++)
            {
                for (int y = 0; y < tileMapResolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * tileMapResolution.y];
                    CellCenter c = generator.cells[currentCellID];

                    if (c.isWater)
                    {
                        texColors[x, y] = water;
                    }
                    else
                    {
                        texColors[x, y] = Color.Lerp(dry, wet, c.moisture);
                    }
                }
            }
        }

        private void DrawBiomes()
        {
            // OPTIONAL: fill grid with default biome (0)
            //tileMap.BoxFill(Vector3Int.zero, biomes[0].tile, 0, 0, resolution.x, resolution.y);

            int mainWidth = tileMapResolution.x;
            int mainHeight = tileMapResolution.y;

            //Debug.Log("res: " + mainWidth + " : " + mainHeight + " tiles");

            //            List<LayerInstance> layerInstances = new List<LayerInstance>();
            //            biomeGrid = new int[mainWidth * mainHeight];

            for (int x = 0; x < mainWidth; x++)
            {
                for (int y = 0; y < mainHeight; y++)
                {
                    // Calculate currentCellID from the main array
                    int index = x + y * mainWidth;
                    //int indexFlipY = x + (mainHeight-y-1) * mainWidth; // works, but then other overlays are flipped in unity
                    int currentCellID = cellIDs[index];

                    CellCenter c = generator.cells[currentCellID];

                    BiomeColor biome = biomes.FirstOrDefault(b => b.biome == c.biome);

                    if (biome != null)
                    {
                        texColors[x, y] = biome.color;
                        tileMap.SetTile(new Vector3Int(x, y, 0), biome.tile);

                        // Use the biome enum value
                        int biomeEnumValue = ((int)biome.biome);

                        tileMap.SetTile(new Vector3Int(x, y, 0), biomes[biomeEnumValue].tile);
                    }
                    else
                    {
                        // Handle missing biome (e.g., use a default biome or color)
                        texColors[x, y] = Color.black;
                    }
                }  // End of x loop
            } // End of y loop
        } // draw biomes

        // TODO remove, or might use for something else
        void ExportLDTK()
        {
            int ldtkLevelTileWidth = 256;  // Each LDTK level quadrant width in tiles
            int ldtkLevelTileHeight = 256; // Each LDTK level quadrant height in tiles
            int totalLdtkLevels = 16;      // Total levels in LDTK (4x4 grid)

            // read tilemap and export to ldtk
            for (int arrayIndex = 0; arrayIndex < totalLdtkLevels; arrayIndex++)
            {
                // Calculate the adjusted index for the 4x4 grid
                int adjustedIndex = arrayIndex;  // Direct mapping to 16 levels for this case

                // Calculate the starting X and Y positions for this LDTK level (256x256 grid)
                int startX = (arrayIndex % 4) * ldtkLevelTileWidth;  // 0, 256, 512, 768 for X
                int startY = (arrayIndex / 4) * ldtkLevelTileHeight; // 0, 256, 512, 768 for Y

                // Loop through each tile in this 256x256 grid (LDTK level)
                //for (int x = 0; x < ldtkLevelTileWidth; x++)
                for (int y = 0; y < ldtkLevelTileHeight; y++)
                //for (int y = ldtkLevelTileHeight; y > 0; y--)
                {
                    for (int x = 0; x < ldtkLevelTileWidth; x++)
                    //for (int x = ldtkLevelTileWidth; x > 0; x--)
                    //for (int y = 0; y < ldtkLevelTileHeight; y++)
                    {
                        // Global X, Y in the 1024x1024 Unity tilemap
                        int globalX = startX + x;
                        int globalY = startY + y;

                        // Get the tile at this position in the Unity tilemap
                        Vector3Int tilePosition = new Vector3Int(globalX, globalY, 0);
                        //TileBase tile = tileMap.GetTile(tilePosition);

                        // Assume you have logic to map Unity tiles to LDTK biome values
                        //int biomeEnumValue = 0;// arrayIndex + 1;
                        //int biomeEnumValue = biomeGrid[globalX + globalY * resolution.x];

                        //if (x < 10) biomeEnumValue = arrayIndex + 1;
                        //tileMap.SetTile(tilePosition, biomes[biomeEnumValue].tile);
                    }
                }
            }
        }

        private void DrawSquare(int x, int y, int size, Color c)
        {
            Rect textureBounds = Rect.MinMaxRect(0, 0, tileMapResolution.x, tileMapResolution.y);

            for (int i = x - size; i < x + size; i++)
            {
                for (int j = y - size; j < y + size; j++)
                {
                    if (textureBounds.Contains(new Vector2(i, j)))
                    {
                        texColors[i, j] = c;
                    }
                }
            }
        }

        private void DrawWireSquare(int x, int y, int size, int thickness, Color c)
        {
            Rect textureBounds = Rect.MinMaxRect(0, 0, tileMapResolution.x, tileMapResolution.y);
            Rect innerRect = new Rect(x - size + thickness, y - size + thickness, size * 2 - thickness * 2, size * 2 - thickness * 2);

            for (int i = x - size; i < x + size; i++)
            {
                for (int j = y - size; j < y + size; j++)
                {
                    Vector2 p = new Vector2(i, j);

                    if (textureBounds.Contains(p) &&
                        !innerRect.Contains(p))
                    {
                        texColors[i, j] = c;
                    }
                }
            }
        }

        private void DrawCircle(int x, int y, int size, Color c)
        {
            for (int i = x - size; i < x + size; i++)
            {
                for (int j = y - size; j < y + size; j++)
                {
                    if (i >= 0 && i < tileMapResolution.x && j >= 0 && j < tileMapResolution.y && Vector2Int.Distance(new Vector2Int(i, j), new Vector2Int(x, y)) <= size)
                    {
                        texColors[i, j] = c;

                        // TODO use water tile? TODO if current biome is winter, use ice
                        tileMap.SetTile(new Vector3Int(i, j, 0), biomes[3].tile);
                        int index = i + j * tileMapResolution.x;
                    }
                }
            }
        }

        private void DrawWireCircle(int x, int y, int size, int thickness, Color c)
        {
            Vector2Int p = new Vector2Int(x, y);

            for (int i = x - size; i < x + size; i++)
            {
                for (int j = y - size; j < y + size; j++)
                {
                    if (i >= 0 &&
                        i < tileMapResolution.x &&
                        j >= 0 &&
                        j < tileMapResolution.y &&
                        Vector2Int.Distance(p, new Vector2Int(i, j)) <= size &&
                        Vector2Int.Distance(p, new Vector2Int(i, j)) > size - thickness)
                    {
                        texColors[i, j] = c;
                    }
                }
            }
        }

        private void DrawLine(int x0, int y0, int x1, int y1, int thickness, Color c)
        {
            Vector2 a = new Vector2(x0, y0);
            Vector2 b = new Vector2(x1, y1);

            float distance = Vector2.Distance(a, b);
            float steps = distance / (thickness / 2f);
            float stepSize = distance / steps;
            Vector2 direction = (b - a).normalized;

            for (float i = 0; i < steps; i++)
            {
                Vector2 pos = new Vector2()
                {
                    x = x0 + direction.x * stepSize * i,
                    y = y0 + direction.y * stepSize * i,
                };

                DrawCircle(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y), thickness, c);
            }

            DrawCircle(x1, y1, thickness, c);
        }

        private void DrawArrow(int x0, int y0, int x1, int y1, int thickness, Color c)
        {
            Vector2 dir = new Vector2(x0, y0) - new Vector2(x1, y1);
            float headSize = dir.magnitude * .3f;
            float arrowHeadAngle = 45;

            DrawLine(x0, y0, x1, y1, thickness, c);

            Vector2 arrowSide1 = Quaternion.AngleAxis(arrowHeadAngle, Vector3.forward) * dir;
            Vector2 arrowSide2 = Quaternion.AngleAxis(-arrowHeadAngle, Vector3.forward) * dir;

            DrawLine(x1, y1, (int)(x1 + arrowSide1.normalized.x * headSize), (int)(y1 + arrowSide1.normalized.y * headSize), thickness, c);
            DrawLine(x1, y1, (int)(x1 + arrowSide2.normalized.x * headSize), (int)(y1 + arrowSide2.normalized.y * headSize), thickness, c);
        }

        private void DrawArrow(int x, int y, Vector2 dir, float lenght, int thickness, Color c)
        {
            DrawArrow(x, y, (int)(x + dir.normalized.x * lenght), (int)(y + dir.normalized.y * lenght), thickness, c);
        }

        private void DrawGraphPoint(MapPoint point, Color c, int size, int thickness = 0)
        {
            Vector2Int pos = MapGraphCoordToTextureCoords(point.position.x, point.position.y);

            if (thickness == 0)
            {
                DrawCircle(pos.x, pos.y, size, c);
            }
            else
            {
                DrawWireCircle(pos.x, pos.y, size, thickness, c);
            }
        }

        private void DrawGraphEdge(CellEdge edge, Color c, int thickness, bool trueForVoronoi)
        {
            if (trueForVoronoi)
            {
                Vector2Int pos0 = MapGraphCoordToTextureCoords(edge.v0.position.x, edge.v0.position.y);
                Vector2Int pos1 = MapGraphCoordToTextureCoords(edge.v1.position.x, edge.v1.position.y);
                DrawLine(pos0.x, pos0.y, pos1.x, pos1.y, thickness, c);
            }
            else
            {
                Vector2Int pos0 = MapGraphCoordToTextureCoords(edge.d0.position.x, edge.d0.position.y);
                Vector2Int pos1 = MapGraphCoordToTextureCoords(edge.d1.position.x, edge.d1.position.y);
                DrawLine(pos0.x, pos0.y, pos1.x, pos1.y, thickness, c);
            }
        }

    } // class
} // namespace