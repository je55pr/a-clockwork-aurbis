namespace AClockworkAurbis.Simulation;

// A reusable, reproducible choice rule: risk widens the range of plausible
// options without ever making an unavailable or unaffordable action valid.
public readonly record struct NpcChoiceOption<T>(T Value, double Cost);

public static class NpcChoicePolicy
{
    public static T Choose<T>(
        IReadOnlyList<NpcChoiceOption<T>> options, int riskTolerance,
        string npcId, int seed, long choiceIndex)
    {
        if(options.Count==0)throw new ArgumentException("At least one option is required");
        if(riskTolerance is <0 or >100)throw new ArgumentOutOfRangeException(nameof(riskTolerance));
        int best=0;
        for(int i=1;i<options.Count;i++)
        {
            if(!double.IsFinite(options[i].Cost))
                throw new ArgumentException("Option costs must be finite");
            if(options[i].Cost<options[best].Cost)best=i;
        }
        if(!double.IsFinite(options[best].Cost))
            throw new ArgumentException("Option costs must be finite");
        if(riskTolerance==0 || options.Count==1)return options[best].Value;

        // Softmax on regret. Small deviations remain possible for ordinary
        // people; higher risk increases experimentation, not omniscience.
        double temperature=3d+0.50d*riskTolerance;
        double[] weights=new double[options.Count];
        double total=0;
        for(int i=0;i<options.Count;i++)
        {
            double regret=options[i].Cost-options[best].Cost;
            weights[i]=Math.Exp(-regret/temperature);
            total+=weights[i];
        }

        // Hash is independent of process-global RNG state and event ordering.
        ulong bits=StableRandom.Hash(
            npcId+"|choice|"+choiceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            unchecked(seed+1279));
        // FNV is suitable for stable IDs but neighbouring numeric suffixes
        // need an avalanche before their high bits are sampled as uniform.
        unchecked
        {
            bits=(bits^(bits>>30))*0xbf58476d1ce4e5b9UL;
            bits=(bits^(bits>>27))*0x94d049bb133111ebUL;
            bits^=bits>>31;
        }
        double target=(bits>>11)*(1.0/(1UL<<53))*total;
        for(int i=0;i<weights.Length;i++)
        {
            target-=weights[i];
            if(target<0)return options[i].Value;
        }
        return options[^1].Value;
    }
}
