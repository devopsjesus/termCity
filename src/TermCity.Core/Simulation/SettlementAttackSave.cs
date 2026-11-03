using System.Numerics;

namespace TermCity.Core.Simulation;

internal sealed record SavedAttack(int Id, WallDefenseKind Kind, int Wall, int Defender, int? Target, bool Melee,
    double Damage, double Elapsed, double Windup, double Travel, bool Applied, double StartX, double StartY,
    double EndX, double EndY, float StartHeight, float EndHeight, float Arc, float DropHeight,
    float CenterX, float CenterY, float ForwardX, float ForwardY, int Station, float OriginX, float OriginY)
{
    internal static SavedAttack Capture(SiegeProjectile shot) => new(shot.Id, shot.Kind, shot.SourceWallId,
        shot.SourceDefenderId, shot.TargetId, shot.Melee, shot.Damage, shot.Elapsed, shot.Windup, shot.TravelSeconds,
        shot.Applied, shot.StartX, shot.StartY, shot.EndX, shot.EndY, shot.WorldStartHeight, shot.WorldEndHeight,
        shot.ArcHeight, shot.DropHeight, shot.FootprintCenter.X, shot.FootprintCenter.Y,
        shot.FootprintForward.X, shot.FootprintForward.Y, shot.StationIndex, shot.CoverageOrigin.X, shot.CoverageOrigin.Y);

    internal SiegeProjectile Restore(CityGame game, int nextId)
    {
        static bool Range(double v, double low, double high) => double.IsFinite(v) && v >= low && v <= high;
        if (!Enum.IsDefined(Kind)) throw new InvalidDataException("Unknown committed attack kind.");
        bool drop = Kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks;
        double expected = Melee ? CombatProfiles.Guard.Damage : Defender > 0 ? CombatProfiles.Archer.Damage : WallDefenses.Get(Kind).Damage;
        if (Id <= 0 || Id >= nextId || !Enum.IsDefined(Kind) || Wall < 0 || Defender < 0 ||
            Target is null or <= 0 || Damage != expected || Melee && (Defender == 0 || Kind != WallDefenseKind.None) ||
            Defender > 0 && Kind != WallDefenseKind.None ||
            !Range(Elapsed, 0, 20) || !Range(Windup, 0, 1) || !Range(Travel, 0, 10) ||
            !Melee && Travel <= 0 || !Range(StartX, 0, game.Map.Width) || !Range(EndX, 0, game.Map.Width) ||
            !Range(StartY, 0, game.Map.Height) || !Range(EndY, 0, game.Map.Height) ||
            !Range(StartHeight, 0, 100) || !Range(EndHeight, 0, 100) || !Range(Arc, 0, 20) ||
            !Range(DropHeight, 0, 100) || !Range(CenterX, 0, game.Map.Width) || !Range(CenterY, 0, game.Map.Height) ||
            !Range(OriginX, 0, game.Map.Width) || !Range(OriginY, 0, game.Map.Height) ||
            !Range(ForwardX, -1, 1) || !Range(ForwardY, -1, 1) ||
            drop && (Station < 0 || Station >= Rendering.TowerDefenseArtwork.MaximumStations ||
                Math.Abs(ForwardX * ForwardX + ForwardY * ForwardY - 1) > .001) ||
            !Applied && Elapsed >= Windup + Travel + .000001)
            throw new InvalidDataException("Invalid committed garrison attack geometry, timing or damage.");
        var shot = new SiegeProjectile
        {
            Id = Id, Kind = Kind, SourceWallId = Wall, SourceDefenderId = Defender, TargetId = Target, Melee = Melee,
            Damage = Damage, Elapsed = Elapsed, Windup = Windup, TravelSeconds = Travel, Applied = Applied,
            StartX = StartX, StartY = StartY, EndX = EndX, EndY = EndY, WorldStartHeight = StartHeight,
            WorldEndHeight = EndHeight, ArcHeight = Arc, DropHeight = DropHeight,
            FootprintCenter = new(CenterX, CenterY), FootprintForward = new(ForwardX, ForwardY), StationIndex = Station,
            CoverageOrigin = new(OriginX, OriginY),
        };
        if (Elapsed >= shot.FlightSeconds + .000001)
            throw new InvalidDataException("A committed attack has expired.");
        shot.Progress = Elapsed / shot.FlightSeconds;
        shot.StartHeight = StartHeight - Rendering.TerrainSprite.Elevation(game.Map,
            Math.Clamp((int)StartX, 0, game.Map.Width - 1), Math.Clamp((int)StartY, 0, game.Map.Height - 1));
        shot.EndHeight = EndHeight - Rendering.TerrainSprite.Elevation(game.Map,
            Math.Clamp((int)EndX, 0, game.Map.Width - 1), Math.Clamp((int)EndY, 0, game.Map.Height - 1));
        return shot;
    }
}
