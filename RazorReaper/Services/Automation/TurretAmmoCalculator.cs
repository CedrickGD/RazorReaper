namespace RazorReaper.Services.Automation;

/// <summary>One ammo type spread over one kind of turret.</summary>
/// <param name="StacksPerTurret">Whole stacks every turret gets in an even split.</param>
/// <param name="AmmoPerTurret">What those stacks hold.</param>
/// <param name="Leftover">Ammo left over after the even split.</param>
/// <param name="TurretsCovered">Turrets that get the full <c>stacksEach</c> from what is on hand (never more than there are).</param>
/// <param name="Missing">Ammo short of giving every turret <c>stacksEach</c> full stacks; 0 when there is enough.</param>
public readonly record struct AmmoPlan(int StacksPerTurret, int AmmoPerTurret, int Leftover, int TurretsCovered, int Missing);

/// <summary>
/// The Turret Manager's ammo calculator. Auto and Heavy turrets take Advanced Rifle Bullets,
/// Tek turrets Element Shards, so it is the same sum twice: whole stacks (the transfer key moves a
/// whole stack) shared evenly, and how far a fixed number of stacks each goes. Stack sizes are
/// inputs because modded servers change them.
/// </summary>
public static class TurretAmmoCalculator
{
    public const int DefaultBulletStack = 100;
    public const int DefaultShardStack = 1000;

    public static AmmoPlan Plan(int turrets, int onHand, int stackSize, int stacksEach)
    {
        turrets = Math.Max(0, turrets);
        onHand = Math.Max(0, onHand);
        stackSize = Math.Max(1, stackSize);
        stacksEach = Math.Max(0, stacksEach);
        if (turrets == 0) return new AmmoPlan(0, 0, onHand, 0, 0);

        var perTurret = onHand / stackSize / turrets;
        var ammoPerTurret = perTurret * stackSize;
        var leftover = onHand - ammoPerTurret * turrets;

        var need = (long)stacksEach * stackSize;
        var covered = need == 0 ? turrets : (int)Math.Min(turrets, onHand / need);
        var missing = (int)Math.Clamp(need * turrets - onHand, 0, int.MaxValue);
        return new AmmoPlan(perTurret, ammoPerTurret, leftover, covered, missing);
    }
}
