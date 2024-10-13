using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using UnityEngine.Tilemaps;
using System.Text;
using ldtk;
using System.IO;
using Newtonsoft.Json;

namespace ProceduralMap
{
    public class PolygonMapDebugView : MonoBehaviour
    {
        public enum ViewBG
        {
            VoronoiCells,
            Shape,
            WaterAndLand,
            Islands,
            Elevation,
            Moisture,
            Biomes,
        }

        [System.Flags] //This attribute turns the enum into a bitmask, and Unity has a special inspactor for bitmasks. We use a bitmask so we can draw more modes at once. Since this is a int, we can have up to 32 values
        public enum Overlays
        {
            VoronoiEdges = 1 << 0, //Bit shifts the bit value of "1" (something like 0001, but with 32 digits, since this is a int) 0 bit to the left
            VoronoiCorners = 1 << 1, //Same as above, but shifting 1 bit to the left, so the result will be "0010" (which is 2 in Decimal)
            DelaunayEdges = 1 << 2,
            DelaunayCorners = 1 << 3,
            Selected = 1 << 4,
            Borders = 1 << 5,
            Coast = 1 << 6,
            Slopes = 1 << 7,
            Rivers = 1 << 8,
        }

        public PolygonMap generator;
        public ComputeShader computeShader;

        [Header("Scene References")]
        public Tilemap tileMap;

        [Header("Options")]
        public Vector2Int resolution = new Vector2Int(512, 512);
        [Tooltip("The size of the tiles in pixels")]
        public int tileSize = 16;
        public ViewBG background;
        public Overlays overlays;
        public int selectedID = -1;
        public BiomeColor[] biomes;

        private Color[,] texColors;
        private RenderTexture rt;
        private int buildTextureKernelIndex;
        private int findClosestCellKernelIndex;
        private int[] cellIDs;

        private const int POINT_SIZE = 5;



        private struct ColorData
        {
            public Vector2Int position;
            public Color color;
        }

        private struct CellData
        {
            public Vector2Int position;
        }

        [System.Serializable]
        public class BiomeColor
        {
            public Biomes biome;
            public Color color;
            public Tile tile;
        }

        private void Awake()
        {
            //Create a render texture and enable random write so we can rend things to it
            rt = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.ARGB32);
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

        #region Generation
        private void GenerateDebugTexture()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only Available in Play Mode");
                return;
            }

            if (tileMap == null || generator == null || generator.cells == null || generator.corners.Count == 0)
                return;

            texColors = new Color[resolution.x, resolution.y];

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

            //We want these to be over everything
            if ((overlays & Overlays.Selected) != 0)
            {
                DrawSelected();
            }
            else
            {
            }

            //Create the texture and assign the texture using a Helper Compute Shader
            ApplyChangesToTexture();
        }
        #endregion

        #region Helper Functions
        private void ApplyChangesToTexture()
        {
            //Create a new Buffer
            ComputeBuffer shaderBuffer = new ComputeBuffer(resolution.x * resolution.y, sizeof(float) * 4 + sizeof(int) * 2); //4 because a color has 4 channels, 2 because the position has X and Y
            ColorData[] colorData = new ColorData[resolution.x * resolution.y];

            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    ColorData data = new ColorData()
                    {
                        position = new Vector2Int(x, y),
                        color = texColors[x, y]
                    };

                    colorData[x + y * resolution.y] = data;
                }
            }

            shaderBuffer.SetData(colorData);
            computeShader.SetBuffer(buildTextureKernelIndex, "_ColorData", shaderBuffer);
            computeShader.SetInt("_Resolution", resolution.x);

            computeShader.Dispatch(buildTextureKernelIndex, resolution.x / 8, resolution.y / 8, 1);
            shaderBuffer?.Dispose();
        }

        private Vector2Int MapGraphCoordToTextureCoords(float x, float y)
        {
            Vector2Int pos = new Vector2Int()
            {
                x = (int)(x / generator.size.x * resolution.x),
                y = (int)(y / generator.size.y * resolution.y)
            };

            return pos;
        }

        private Vector2 MapTextureCoordToGraphCoords(int x, int y)
        {
            Vector2 pos = new Vector2()
            {
                x = (x / (float)resolution.x) * generator.size.x,
                y = (y / (float)resolution.y) * generator.size.y
            };

            return pos;
        }

        private string GetInfoFromCell()
        {
            CellCenter c = generator.cells.First(x => x.index == selectedID);

            string info = $"CELL:\n";

            info += "\n- " + string.Join("\n- ",
                $"id: {c.index}",
                $"elevation: {c.elevation}",
                $"moisture: {c.moisture}",
                $"isCoast: {c.isCoast}",
                $"isWater: {c.isWater}",
                $"isOcean: {c.isOcean}",
                $"isBorder: {c.isBorder}"
            );

            info += "\n\nEDGES:\n";

            foreach (var edge in c.borderEdges)
            {
                info += "\n- " + string.Join("\n",
                    $"id: {edge.index}"
                );
            }

            info += "\n\nCORNERS:\n";

            foreach (var corner in c.cellCorners)
            {
                info += "\n- " + string.Join("\n",
                    $"id: {corner.index}",
                    $"elevation: {corner.elevation}"
                );
            }

            return info;
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
            ComputeBuffer cellIdByPixeldBuffer = new ComputeBuffer(resolution.x * resolution.y, sizeof(int)); //This is the buffer we're gonna read from

            for (int i = 0; i < cellData.Length; i++)
            {
                CellCenter c = generator.cells[i];
                cellData[i] = new CellData()
                {
                    position = MapGraphCoordToTextureCoords(c.position.x, c.position.y)
                };
            }

            cellDataBuffer.SetData(cellData);
            cellIdByPixeldBuffer.SetData(new int[resolution.x * resolution.y]); //we pass an empty array, since we just want to retrieve this data
            computeShader.SetBuffer(findClosestCellKernelIndex, "_CellData", cellDataBuffer);
            computeShader.SetBuffer(findClosestCellKernelIndex, "_CellIDByPixel", cellIdByPixeldBuffer);
            computeShader.SetInt("_Resolution", resolution.x);

            computeShader.Dispatch(findClosestCellKernelIndex, resolution.x / 8, resolution.y / 8, 1);

            //Get the result data back from the GPU
            int[] centersIDs = new int[resolution.x * resolution.y];
            cellIdByPixeldBuffer.GetData(centersIDs);

            cellDataBuffer?.Dispose();
            cellIdByPixeldBuffer?.Dispose();

            return centersIDs;
        }
        #endregion

        #region Draw Views
        private void DrawVoronoiCells()
        {
            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * resolution.y];
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

        private void DrawSelected()
        {
            CellCenter c = generator.cells[selectedID];
            DrawGraphPoint(c, Color.green, POINT_SIZE * 3, 2);

            for (int i = 0; i < c.neighborCells.Count; i++)
            {
                DrawGraphPoint(c.neighborCells[i], Color.magenta, POINT_SIZE * 2, 2);
            }

            for (int i = 0; i < c.cellCorners.Count; i++)
            {
                DrawGraphPoint(c.cellCorners[i], Color.yellow, POINT_SIZE * 2, 2);
            }

            for (int i = 0; i < c.borderEdges.Count; i++)
            {
                DrawGraphEdge(c.borderEdges[i], new Color(0, .5f, .5f), 1, false);
                DrawGraphEdge(c.borderEdges[i], Color.cyan, 1, true);
            }
        }

        private void DrawMapBorders()
        {
            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    CellCenter c = generator.cells[cellIDs[x + y * resolution.y]];

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
            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    texColors[x, y] = generator.shape.IsPointInsideShape(new Vector2(x, y), resolution, generator.seed) ? Color.gray : Color.black;
                }
            }
        }

        private void DrawWaterAndLand()
        {
            Color ocean = new Color(.1f, .1f, .5f);
            Color water = new Color(.5f, .5f, .7f);
            Color land = new Color(.7f, .7f, .5f);
            Color coast = new Color(.5f, .5f, .3f);

            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * resolution.y];
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

            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * resolution.y];
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

            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    CellCenter c = generator.cells[cellIDs[x + y * resolution.y]];

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

            for (int x = 0; x < resolution.x; x++)
            {
                for (int y = 0; y < resolution.y; y++)
                {
                    int currentCellID = cellIDs[x + y * resolution.y];
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

        public static List<int[]> SplitArray(int[] mainArray, int mainWidth, int mainHeight, int subWidth, int subHeight)
        {
            List<int[]> subArrays = new List<int[]>();

            for (int startY = 0; startY < mainHeight; startY += subHeight)
            {
                for (int startX = 0; startX < mainWidth; startX += subWidth)
                {
                    int[] subArray = new int[subWidth * subHeight];
                    ExtractSubArray(mainArray, subArray, mainWidth, startX, startY, subWidth, subHeight);
                    subArrays.Add(subArray);
                }
            }

            return subArrays;
        }

        private static void ExtractSubArray(int[] sourceArray, int[] targetArray, int sourceWidth, int startX, int startY, int targetWidth, int targetHeight)
        {
            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    targetArray[y * targetWidth + x] = sourceArray[(startY + y) * sourceWidth + (startX + x)];
                }
            }
        }

        private void DrawBiomes()
        {
            var levelFile = "Assets/Levels/temp.ldtk";

            if (File.Exists(levelFile) == false)
            {
                Debug.LogError("File not found: " + levelFile);
                return;
            }

            // load ldtk json file
            var jsonString = System.IO.File.ReadAllText(levelFile);
            var ldtkJson = LdtkJson.FromJson(jsonString);

            // OPTIONAL: fill grid with default biome (0)
            //tileMap.BoxFill(Vector3Int.zero, biomes[0].tile, 0, 0, resolution.x, resolution.y);

            List<IntGridValueDefinition> intGridValues = new List<IntGridValueDefinition>();

            for (int i = 0; i < biomes.Length; i++)
            {
                BiomeColor biomeColor = biomes[i];

                // Create a new IntGridValueDefinition object
                IntGridValueDefinition intGridValue = new IntGridValueDefinition
                {
                    Value = i,
                    Identifier = biomeColor.biome.ToString(), // The biome enum as a string identifier
                    Color = ColorToHex(biomeColor.color), // Convert color to hex string
                    Tile = null, // Assuming you don't have tile information here
                    GroupUid = 0 // Defaulting to 0 as per your initial logic
                };

                // Add the object to the list
                intGridValues.Add(intGridValue);
            }

            // assign types into json
            ldtkJson.Defs.Layers[0].IntGridValues = intGridValues.ToArray();

            int mainWidth = resolution.x;
            int mainHeight = resolution.y;

            Debug.Log("res: " + mainWidth + " : " + mainHeight + " tiles");

            List<LayerInstance> layerInstances = new List<LayerInstance>();

            int[] biomeGrid = new int[mainWidth * mainHeight];

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
                        //tileMap.SetTile(new Vector3Int(globalX, globalY, 0), biomes[arrayIndex + 1].tile); // debug

                        // Use the biome enum value
                        int biomeEnumValue = ((int)biome.biome);

                        //biomeEnumValue = 0;
                        //if (x == 0)
                        //{
                        //    biomeEnumValue = 1;
                        //}

                        //if (y == 0)
                        //{
                        //    biomeEnumValue = 2;
                        //}

                        //if (x == mainWidth - 1)
                        //{
                        //    biomeEnumValue = 4;
                        //}

                        //if (y == mainHeight - 1)
                        //{
                        //    biomeEnumValue = 3;
                        //}

                        tileMap.SetTile(new Vector3Int(x, y, 0), biomes[biomeEnumValue].tile);

                        biomeGrid[index] = biomeEnumValue;
                        // TODO fix y flip vs ldtk
                        //biomeGrid[indexFlipY] = biomeEnumValue;
                        //intGridCsv.Add(biomeEnumValue);
                        //intGridCsv.Add(arrayIndex + 1);
                        // 0 is undefined (not assigned), items start from 1 in the ldtk
                        //intGridCsv.Add(biomeEnumValue);
                    }
                    else
                    {
                        // Handle missing biome (e.g., use a default biome or color)
                        texColors[x, y] = Color.black;
                        biomeGrid[currentCellID] = 0;
                    }
                }  // End of x loop
            } // End of y loop

            //LayerInstance layerInstance = ldtkJson.Levels[arrayIndex].LayerInstances[0];
            //layerInstance.IntGridCsv = intGridCsv.ToArray();

            //Debug.Log("arrayIndex: " + (arrayIndex + 1) + " " + ldtkJson.Levels.Length + " " + biomes[arrayIndex + 1].biome.ToString());
            //ldtkJson.Levels[subArrays.Count - 1 - arrayIndex].LayerInstances[0].IntGridCsv = intGridCsv.ToArray();
            //ldtkJson.Levels[arrayIndex].LayerInstances[0].IntGridCsv = intGridCsv.ToArray();

            // TODO export tilemap here
            int unityTilemapWidth = 1024;  // Unity tilemap width in tiles (1024x1024)
            int unityTilemapHeight = 1024; // Unity tilemap height in tiles
            int ldtkLevelTileWidth = 256;  // Each LDTK level quadrant width in tiles
            int ldtkLevelTileHeight = 256; // Each LDTK level quadrant height in tiles
            int totalLdtkLevels = 16;      // Total levels in LDTK (4x4 grid)

            Debug.Log("unity tilemapsize in cells: " + tileMap.size);

            // Tilemap unityTilemap = yourTilemap;  // Reference your Unity Tilemap
            //LdtkJson ldtkJson = LoadLdtkJson("Assets/Levels/temp.ldtk");  // Load the LDTK file

            // Iterate through the 16 quadrants (4x4 grid) in Unity's tilemap
            for (int arrayIndex = 0; arrayIndex < totalLdtkLevels; arrayIndex++)
            {
                // Calculate the adjusted index for the 4x4 grid
                int adjustedIndex = arrayIndex;  // Direct mapping to 16 levels for this case

                // Calculate the starting X and Y positions for this LDTK level (256x256 grid)
                int startX = (arrayIndex % 4) * ldtkLevelTileWidth;  // 0, 256, 512, 768 for X
                int startY = (arrayIndex / 4) * ldtkLevelTileHeight; // 0, 256, 512, 768 for Y

                List<long> intGridCsv = new List<long>();  // For storing biome/tile data

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
                        int biomeEnumValue = biomeGrid[globalX + globalY * mainWidth];

                        //if (x < 10) biomeEnumValue = arrayIndex + 1;

                        //tileMap.SetTile(tilePosition, biomes[biomeEnumValue].tile);
                        tileMap.SetTile(tilePosition, biomes[biomeEnumValue].tile);

                        // Add biomeEnumValue to intGridCsv
                        intGridCsv.Add(biomeEnumValue);
                    }
                }

                // Assign intGridCsv to the corresponding LDTK level's LayerInstance
                ldtkJson.Levels[adjustedIndex].LayerInstances[0].IntGridCsv = intGridCsv.ToArray();
            }

            //sb.AppendLine("]");

            // save new json, formatted, FIXME gridarray is all newlines one by one..
            // BUG: ldtk fails to read? [ERROR]        Cannot read properties of null (reading 'externalLevels') (TypeError)      [ERROR] TypeError: Cannot read properties of null(reading 'externalLevels')
            //string jsonOutput = JsonConvert.SerializeObject(ldtkJson, Formatting.Indented);
            string jsonOutput = ldtkJson.ToJson();
            System.IO.File.WriteAllText("Assets/Levels/temp_new.ldtk", jsonOutput);


        } // draw biomes
        #endregion

        public string ColorToHex(Color color)
        {
            return $"#{(int)(color.r * 255):X2}{(int)(color.g * 255):X2}{(int)(color.b * 255):X2}";
        }

        #region Draw Shapes
        private void DrawSquare(int x, int y, int size, Color c)
        {
            Rect textureBounds = Rect.MinMaxRect(0, 0, resolution.x, resolution.y);

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
            Rect textureBounds = Rect.MinMaxRect(0, 0, resolution.x, resolution.y);
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
                    if (i >= 0 &&
                        i < resolution.x &&
                        j >= 0 &&
                        j < resolution.y &&
                        Vector2Int.Distance(new Vector2Int(i, j), new Vector2Int(x, y)) <= size)
                    {
                        texColors[i, j] = c;
                        //var cc = c;
                        //c.a = 1;
                        //myTex.SetPixel(i, j, c);

                        tileMap.SetTile(new Vector3Int(i, j, 0), biomes[2].tile);
                    }
                }
            }

            // myTex.Apply(false);
        }

        private void DrawWireCircle(int x, int y, int size, int thickness, Color c)
        {
            Vector2Int p = new Vector2Int(x, y);

            for (int i = x - size; i < x + size; i++)
            {
                for (int j = y - size; j < y + size; j++)
                {
                    if (i >= 0 &&
                        i < resolution.x &&
                        j >= 0 &&
                        j < resolution.y &&
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
        #endregion

        private void OnValidate()
        {
            if (generator == null)
                return;

            if (selectedID < 0)
                selectedID = 0;

            if (selectedID >= generator.polygonCount)
                selectedID = generator.polygonCount - 1;

            if (Application.isPlaying)
                GenerateDebugTexture();
        }
    }
}