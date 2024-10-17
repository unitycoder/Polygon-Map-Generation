namespace ProceduralMap
{
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
}