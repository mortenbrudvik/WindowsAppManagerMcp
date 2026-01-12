namespace WindowsAppManagerMcp.Models;

public record WindowRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static WindowRect FromLTRB(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);
}
