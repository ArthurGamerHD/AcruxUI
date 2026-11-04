namespace Acrux.Render3D
{
    /// <summary>Kinds of functional blocks an X-ray view of a grid can single out.</summary>
    public enum GridBlockCategory : byte
    {
        /// <summary>No X-ray: the grid as it is. Also every block outside the categories below.</summary>
        None,
        /// <summary>Thrusters of every kind.</summary>
        Thrusters,
        /// <summary>Power producers: reactors, batteries, solar panels, engines, turbines.</summary>
        Power,
        /// <summary>Gyroscopes.</summary>
        Gyroscopes,
        /// <summary>Guns, turrets, launchers and warheads.</summary>
        Weapons,
        /// <summary>Gravity generators and artificial masses.</summary>
        Gravity,
        /// <summary>Production blocks: assemblers, refineries, survival kits and the like.</summary>
        Production,
        /// <summary>Cargo containers.</summary>
        Cargo,
        /// <summary>Conveyor junctions, tubes and sorters.</summary>
        Conveyors
    }
}
