﻿// original source: https://github.com/DeiveEx/Polygon-Map-Generation
using System.Collections.Generic;
using UnityEngine;
using NaughtyAttributes;
using csDelaunay; //The Voronoi Library
using System.Linq;

namespace ProceduralMap
{
    public class PolygonMap : MonoBehaviour
    {
        public bool useCustomSeed = true;

        [Header("Map")]
        public int seed;
        public int polygonCount = 2048; //The number of polygons/sites we want
        public Vector2 size = new Vector2(2, 2);
        public int relaxation = 4;
        public Shape islandShape;
        public AnimationCurve elevationCurve = AnimationCurve.Linear(0, 0, 1, 1);
        public IslandShape shape;
        public bool singleIsland;
        public int minIslandSize;

        [Header("Rivers")]
        public int springsSeed = 3;
        public int numberOfSprings = 15;
        [Range(0, 1)] public float minSpringElevation = 0.3f;
        [Range(0, 1)] public float maxSpringElevation = 0.9f;

        //Graphs. Here are the info necessary to build both the Voronoi Graph than the Delaunay triangulation Graph
        public List<CellCenter> cells = new List<CellCenter>(); //The center of each cell makes up a corner for the Delaunay triangles
        public List<CellCorner> corners = new List<CellCorner>(); //The corners of the cells. Also the center of the Delaunay triangles
        public List<CellEdge> edges = new List<CellEdge>(); //We use a single object here, but we are representing two edges for each object (the voronoi edge and the Delaunay Edge)
        public List<List<CellCenter>> islands = new List<List<CellCenter>>();

        //Events
        public event System.Action onMapGenerated;

        //Constants
        private const float LAKE_THRESHOLD = 0.3f; //0 to 1. Percentage of how many corners must be water for a cell center to be water too

        private void Start()
        {
            Generate();
        }

        [Button]
        public void Generate()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Only Available in Play Mode");
                return;
            }

            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            //Clear all map info
            ResetMapInfo();

            if (useCustomSeed == false)
            {
                seed = Random.Range(int.MinValue, int.MaxValue);
            }

            //Set the seed for the random system
            Random.InitState(seed);

            List<Vector2> points = GenerateRandomPoints();
            GenerateGraphs(points);
            AssignWater(); //He is where we define the general shape of the island
            AssignOceanCoastAndLand();
            DetectIslands();
            AssignOceanCoastAndLandCorners();
            AssignElevations(); //For this case, we are making that the farthest from the coast, the higher the elevation
            AddRivers();
            AssignMoisture();
            AssignBiome();

            //Execute an event saying we finished our generation
            onMapGenerated?.Invoke();

            // new seed for next generation
            seed++;

            stopwatch.Stop();
            Debug.LogFormat("Timer: {0} ms", stopwatch.ElapsedMilliseconds);
            stopwatch.Reset();
        }

        private void ResetMapInfo()
        {
            corners.Clear();
            cells.Clear();
            edges.Clear();
        }

        private List<Vector2> GenerateRandomPoints()
        {
            //Generate random points
            List<Vector2> points = new List<Vector2>();

            for (int i = 0; i < polygonCount; i++)
            {
                Vector2 p = new Vector2()
                {
                    x = Random.Range(0, size.x),
                    y = Random.Range(0, size.y)
                };

                points.Add(p);
            }

            return points;
        }

        private void GenerateGraphs(List<Vector2> points)
        {
            //Generate the Voronoi
            Rectf bounds = new Rectf(0, 0, size.x, size.y);
            Voronoi voronoi = new Voronoi(points, bounds, relaxation);

            //Cell centers
            foreach (var site in voronoi.SitesIndexedByLocation)
            {
                CellCenter c = new CellCenter();
                c.index = cells.Count;
                c.position = site.Key;

                cells.Add(c);
            }

            //Cell Corners
            foreach (var edge in voronoi.Edges)
            {
                //If the edge doesn't have clipped ends, it was not withing bounds
                if (edge.ClippedEnds == null)
                    continue;

                if (!corners.Any(x => x.position == edge.ClippedEnds[LR.LEFT]))
                {
                    CellCorner c = new CellCorner();
                    c.index = corners.Count;
                    c.position = edge.ClippedEnds[LR.LEFT];
                    c.isBorder = c.position.x == 0 || c.position.x == size.x || c.position.y == 0 || c.position.y == size.y;

                    corners.Add(c);
                }

                if (!corners.Any(x => x.position == edge.ClippedEnds[LR.RIGHT]))
                {
                    CellCorner c = new CellCorner();
                    c.index = corners.Count;
                    c.position = edge.ClippedEnds[LR.RIGHT];
                    c.isBorder = c.position.x == 0 || c.position.x == size.x || c.position.y == 0 || c.position.y == size.y;

                    corners.Add(c);
                }
            }

            //Define some local helper functions to help with the loop below
            void AddPointToPointList<T>(List<T> list, T point) where T : MapPoint
            {
                if (!list.Contains(point))
                    list.Add(point);
            }

            //Voronoi and Delaunay edges. Each edge point to two cells and two corners, so we can store both the sites and corners into a single edge object (thus making two edges into one object)
            foreach (var voronoiEdge in voronoi.Edges)
            {
                if (voronoiEdge.ClippedEnds == null)
                    continue;

                CellEdge edge = new CellEdge();
                edge.index = edges.Count;

                //Set the voronoi edge
                edge.v0 = corners.First(x => x.position == voronoiEdge.ClippedEnds[LR.LEFT]);
                edge.v1 = corners.First(x => x.position == voronoiEdge.ClippedEnds[LR.RIGHT]);

                //Set the Delaunay edge
                edge.d0 = cells.First(x => x.position == voronoiEdge.LeftSite.Coord);
                edge.d1 = cells.First(x => x.position == voronoiEdge.RightSite.Coord);

                edges.Add(edge);

                /*Set the relationships*/

                //Set the relationship between this edge and the connected cells centers/corners
                edge.d0.borderEdges.Add(edge);
                edge.d1.borderEdges.Add(edge);
                edge.v0.connectedEdges.Add(edge);
                edge.v1.connectedEdges.Add(edge);

                //Set the relationship between the CELL CENTERS connected to this edge
                AddPointToPointList(edge.d0.neighborCells, edge.d1);
                AddPointToPointList(edge.d1.neighborCells, edge.d0);

                //Set the relationship between the CORNERS connected to this edge
                AddPointToPointList(edge.v0.neighborCorners, edge.v1);
                AddPointToPointList(edge.v1.neighborCorners, edge.v0);

                //Set the relationship of the CORNERS connected to this edge and the CELL CENTERS connected to this edge
                AddPointToPointList(edge.d0.cellCorners, edge.v0);
                AddPointToPointList(edge.d0.cellCorners, edge.v1);

                AddPointToPointList(edge.d1.cellCorners, edge.v0);
                AddPointToPointList(edge.d1.cellCorners, edge.v1);

                //Same as above, but the other way around
                AddPointToPointList(edge.v0.touchingCells, edge.d0);
                AddPointToPointList(edge.v0.touchingCells, edge.d1);

                AddPointToPointList(edge.v1.touchingCells, edge.d0);
                AddPointToPointList(edge.v1.touchingCells, edge.d1);
            }
        }

        private void AssignWater()
        {
            //Define if a corner is land or not based on some shape function
            foreach (var corner in corners)
            {
                corner.isWater = !shape.IsPointInsideShape(corner.position, size, seed);
            }
        }

        private void AssignOceanCoastAndLand()
        {
            Queue<CellCenter> queue = new Queue<CellCenter>();
            int numWater = 0;

            //Set the cell to water / ocean / border
            foreach (var center in cells)
            {
                numWater = 0;

                foreach (var corner in center.cellCorners)
                {
                    if (corner.isBorder) //If a corner connected to this cell is at the map border, then this cell is a ocean and the corner itself is water
                    {
                        center.isBorder = true;
                        center.isOcean = true;
                        corner.isWater = true;
                        queue.Enqueue(center);
                    }

                    if (corner.isWater)
                    {
                        numWater += 1;
                    }
                }

                //If the amount of corners on this cell is grater than the defined threshold, this cell is water
                center.isWater = center.isOcean || numWater >= center.cellCorners.Count * LAKE_THRESHOLD;
            }

            //Every cell around a border must be a ocean too, and we loop thought the neighbors until can't find more water (at which case, the queue would be empty)
            while (queue.Count > 0)
            {
                CellCenter c = queue.Dequeue();

                foreach (var n in c.neighborCells)
                {
                    if (n.isWater && !n.isOcean)
                    {
                        n.isOcean = true;
                        queue.Enqueue(n); //If this neighbor is a ocean, we add it to the queue so wwe can check its neighbors
                    }
                }
            }

            //Set the Coast cells based on the neighbors. If a cell has at least one ocean and one land neighbor, then the cell is a coast
            foreach (var cell in cells)
            {
                int numOcean = 0;
                int numLand = 0;

                foreach (var n in cell.neighborCells)
                {
                    numOcean += n.isOcean ? 1 : 0;
                    numLand += !n.isWater ? 1 : 0;
                }

                cell.isCoast = numOcean > 0 && numLand > 0;
            }
        }

        private void AssignOceanCoastAndLandCorners()
        {
            //Set the corners attributes based on the connected cells. If all connected cells are ocean, then the corner is ocean. If all cells are land, then the corner is land. Otherwise the corner is a coast
            foreach (var corner in corners)
            {
                int numOcean = 0;
                int numLand = 0;

                foreach (var cell in corner.touchingCells)
                {
                    numOcean += cell.isOcean ? 1 : 0;
                    numLand += !cell.isWater ? 1 : 0;
                }

                corner.isOcean = numOcean == corner.touchingCells.Count;
                corner.isCoast = numOcean > 0 && numLand > 0;
                corner.isWater = corner.isBorder || numLand != corner.touchingCells.Count && !corner.isCoast;
            }
        }

        private void DetectIslands()
        {
            List<CellCenter> land = new List<CellCenter>();

            foreach (var cell in cells)
            {
                if (cell.isOcean)
                {
                    cell.islandID = -1; //Ocean tiles doesn't have a island
                }
                else
                {
                    land.Add(cell);
                }
            }

            islands = new List<List<CellCenter>>();

            for (int i = 0; i < land.Count; i++)
            {
                CellCenter currentCell = land[i];

                //Is the current cell in any island already?
                if (!islands.Any(x => x.Contains(currentCell)))
                {
                    //If not, create a new island for it and add the current cell
                    List<CellCenter> currentIsland = new List<CellCenter>();
                    islands.Add(currentIsland);

                    currentIsland.Add(currentCell);
                    currentCell.islandID = islands.Count - 1;

                    //Create a queue with the current cell. We check its neighbors to see if they are not an ocean tile. If not, check if it was already added to the current island.
                    Queue<CellCenter> islandQueue = new Queue<CellCenter>();
                    islandQueue.Enqueue(currentCell);

                    while (islandQueue.Count > 0)
                    {
                        currentCell = islandQueue.Dequeue();

                        foreach (var neighbor in currentCell.neighborCells)
                        {
                            if (!neighbor.isOcean && !currentIsland.Contains(neighbor))
                            {
                                islandQueue.Enqueue(neighbor); //Add the neighbor to the queue so we can then check its neighbors, until we can't find any neightbor that is either a ocean or was not added to the current island
                                currentIsland.Add(neighbor);
                                neighbor.islandID = islands.Count - 1;
                            }
                        }
                    }
                }
            }

            if (singleIsland)
            {
                minIslandSize = islands.Max(x => x.Count);
            }

            //Remove all islands that have a lower cell count than the minimum size
            for (int i = 0; i < islands.Count; i++)
            {
                List<CellCenter> currentIsland = islands[i];

                if (currentIsland.Count < minIslandSize)
                {
                    foreach (var cell in currentIsland)
                    {
                        //We transform all cells in the islands we want to discard into ocean cells
                        cell.isWater = true;
                        cell.isOcean = true;
                        cell.islandID = -1;
                    }

                    //Remove the current island
                    islands.RemoveAt(i);
                    i--;
                }
            }
        }

        private void AssignElevations()
        {
            List<CellCorner> queue = new List<CellCorner>(); //We have to use a List<T> instead of a Queue<T> because we need to add itens both at the begging and a the end of the list
            float minElevation = 1, maxElevation = 1;

            //Find all coast corners and assign their elevation to 0
            foreach (var corner in corners)
            {
                if (corner.isCoast)
                {
                    queue.Add(corner);
                    corner.elevation = 0;
                }
                else
                {
                    corner.elevation = Mathf.Infinity;
                }
            }

            //Define some helper functions to help with the loop below
            bool IsCellLake(CellCenter c)
            {
                return c.isWater && !c.isOcean;
            }

            bool IsEdgeLake(CellEdge e)
            {
                return IsCellLake(e.d0) || IsCellLake(e.d1);
            }

            while (queue.Count > 0)
            {
                CellCorner currentCorner = queue[0]; //Get the fisrt item on the list
                queue.RemoveAt(0); //Remove the item from the list
                int offset = Random.Range(0, currentCorner.connectedEdges.Count); //Add a random offset to the iterator

                for (int i = 0; i < currentCorner.connectedEdges.Count; i++)
                {
                    CellEdge e = currentCorner.connectedEdges[(i + offset) % currentCorner.connectedEdges.Count]; //uses the offset to start at a random edge, but still circulate through all of them
                    CellCorner neighbor = e.v0 == currentCorner ? e.v1 : e.v0; //Get the corner that is part of this edge and opposite of the current corner
                    float newElevation = (IsEdgeLake(e) ? 0 : 1) + currentCorner.elevation;

                    //If the neighboor has a higher elevation than the calculated one, we have to change the elevation (in other words, we always use the lowest calculated elevation value)
                    if (newElevation < neighbor.elevation)
                    {
                        neighbor.elevation = newElevation;
                        neighbor.downslopeCorner = currentCorner; //Since this elevation is (corner elevation + (0 || 1)), that means this corner is either higher or the same height as the current corner, and so we can set the parent corner as the downslope
                        neighbor.downslopeEdge = e;

                        //Update the min/max elevations
                        if (neighbor.isOcean && newElevation > minElevation)
                            minElevation = newElevation;

                        if (!neighbor.isOcean && newElevation > maxElevation)
                            maxElevation = newElevation;

                        //If this corner was a lake, we have to revisit it again to guarantee that all edges of a lake has the same elevation
                        if (IsEdgeLake(e))
                        {
                            queue.Insert(0, neighbor);
                        }
                        else
                        {
                            queue.Add(neighbor);
                        }
                    }
                }
            }

            //Normalize the elevations so we have a range from 0 to 1 for land/lakes, and -1 to 0 for oceans
            foreach (var corner in corners)
            {
                if (!corner.isOcean)
                {
                    corner.elevation = elevationCurve.Evaluate(corner.elevation / maxElevation);
                }
                else
                {
                    corner.elevation = -elevationCurve.Evaluate(corner.elevation / minElevation);
                }
            }

            //Set the cell center elevation to be the average of its corners. Also, since the coastline is at elevation 0, if some ocean is greater than it, we override the value
            float maxOceanElevation = -0.01f;

            foreach (var center in cells)
            {
                float sumElevations = 0;

                foreach (var corner in center.cellCorners)
                {
                    sumElevations += corner.elevation;
                }

                center.elevation = sumElevations / center.cellCorners.Count;

                //make sure that ocean cells won't be on a higher elevation than the coast
                if (center.isOcean && center.elevation > maxOceanElevation)
                {
                    center.elevation = maxOceanElevation;
                }
            }
        }

        private void AddRivers()
        {
            List<CellCorner> springs = new List<CellCorner>();

            //Get all corners that can possibly be a spring.
            for (int i = 0; i < corners.Count; i++)
            {
                if (corners[i].elevation >= minSpringElevation && corners[i].elevation <= maxSpringElevation && !corners[i].isWater)
                {
                    springs.Add(corners[i]);
                }
            }

            //Select some corners randomly from the previous list to be used as our springs
            List<CellCorner> rivers = new List<CellCorner>();

            springsSeed = useCustomSeed ? springsSeed : Random.Range(0, 10000);
            Random.InitState(springsSeed);

            while (springs.Count > 0 && rivers.Count < numberOfSprings)
            {
                int id = Random.Range(0, springs.Count);
                rivers.Add(springs[id]);
                springs.RemoveAt(id);
            }

            //Assign the flow of the river for each edge using their downslope. Each time a edge is assigned as a river, the water volume is increased by 1, so if two rivers join together, the next edge will have a bigger volume of water
            foreach (var river in rivers)
            {
                CellCorner currentRiverCorner = river;

                while (true)
                {
                    //If the current corner doesn't have a downslope, then we reached the coast
                    if (currentRiverCorner.downslopeCorner == null)
                        break;

                    //Increase the water volume for the downslope edge
                    currentRiverCorner.downslopeEdge.waterVolume++;

                    //Set the current river corner to be the dowsnlope, so we can keep going down until we reach the coast
                    currentRiverCorner = currentRiverCorner.downslopeCorner;
                }
            }
        }

        private void AssignMoisture()
        {
            HashSet<CellCenter> moistureSeeds = new HashSet<CellCenter>(); //we use a HashSet to guarantee we won't have duplicate values

            foreach (var edge in edges)
            {
                //Find all riverbanks (regions adjacent to rivers)
                if (edge.waterVolume > 0)
                {
                    moistureSeeds.Add(edge.d0);
                    moistureSeeds.Add(edge.d1);
                }

                //Find all lakeshores (regions adjacent to lakes)
                if ((edge.d0.isWater && !edge.d0.isOcean) || (edge.d1.isWater && !edge.d1.isOcean))
                {
                    moistureSeeds.Add(edge.d0);
                    moistureSeeds.Add(edge.d1);
                }
            }

            //Copy the hashset values to a queue
            Queue<CellCenter> queue = new Queue<CellCenter>(moistureSeeds);
            Dictionary<int, float> waterDistance = new Dictionary<int, float>();

            //Set the distance of each cell in the queue to 0, since they're the closest to a water source. Any other cell gets -1
            foreach (var cell in cells)
            {
                if (queue.Contains(cell))
                {
                    waterDistance.Add(cell.index, 0);
                }
                else
                {
                    waterDistance.Add(cell.index, -1);
                }
            }

            float maxDistance = 1;

            while (queue.Count > 0)
            {
                CellCenter currentCell = queue.Dequeue();

                foreach (var neighbor in currentCell.neighborCells)
                {
                    if (!neighbor.isWater && waterDistance[neighbor.index] < 0)
                    {
                        float newDistance = waterDistance[currentCell.index] + 1;
                        waterDistance[neighbor.index] = newDistance;

                        if (newDistance > maxDistance)
                            maxDistance = newDistance;

                        queue.Enqueue(neighbor);
                    }
                }
            }

            //Normalize the moisture values
            foreach (var cell in cells)
            {
                cell.moisture = cell.isWater ? 1 : 1 - (waterDistance[cell.index] / maxDistance);
            }

            ////Redistribute moisture values evenly
            //List<CellCenter> land = cells.Where(x => !x.isWater).ToList();
            //land.Sort((x, y) =>
            //{
            //	if (x.moisture < y.moisture)
            //		return -1;

            //	if (x.moisture > y.moisture)
            //		return 1;

            //	return 0;
            //});

            //float minMoisture = 0;
            //float maxMoisture = 1;

            //for (int i = 0; i < land.Count; i++)
            //{
            //	land[i].moisture = minMoisture + (maxMoisture - minMoisture) * i / (land.Count - 1);
            //}
        }

        private void AssignBiome()
        {
            foreach (var cell in cells)
            {
                if (cell.isOcean)
                {
                    cell.biome = Biomes.Ocean;
                }
                else if (cell.isWater)
                {
                    if (cell.elevation < .1f)
                        cell.biome = Biomes.Marsh;
                    else if (cell.elevation > .8f)
                        cell.biome = Biomes.Ice;
                    else
                        cell.biome = Biomes.Lake;
                }
                else if (cell.isCoast)
                {
                    cell.biome = Biomes.Beach;
                }
                else if (cell.elevation > .8f)
                {
                    if (cell.moisture > .5f)
                        cell.biome = Biomes.Snow;
                    else if (cell.moisture > .33)
                        cell.biome = Biomes.Tundra;
                    else if (cell.moisture > .16)
                        cell.biome = Biomes.Bare;
                    else
                        cell.biome = Biomes.Scorched;
                }
                else if (cell.elevation > .6)
                {
                    if (cell.moisture > .66)
                        cell.biome = Biomes.Taiga;
                    else if (cell.moisture > .33)
                        cell.biome = Biomes.Shrubland;
                    else
                        cell.biome = Biomes.Temperate_Desert;
                }
                else if (cell.elevation > .3)
                {
                    if (cell.moisture > .83)
                        cell.biome = Biomes.Temperate_Rain_Forest;
                    else if (cell.moisture > .5)
                        cell.biome = Biomes.Temperate_Deciduous_Forest;
                    else if (cell.moisture > .16)
                        cell.biome = Biomes.Grassland;
                    else
                        cell.biome = Biomes.Temperate_Desert;
                }
                else
                {
                    if (cell.moisture > .66)
                        cell.biome = Biomes.Tropical_Rain_Forest;
                    else if (cell.moisture > .33)
                        cell.biome = Biomes.Tropical_Seasonal_Forest;
                    else if (cell.moisture > .16)
                        cell.biome = Biomes.Grassland;
                    else
                        cell.biome = Biomes.Subtropical_Desert;
                }
            }
        }

        private void OnValidate()
        {
            if (cells != null)
            {
                onMapGenerated?.Invoke();
            }
        }

    } // class
} // namespace