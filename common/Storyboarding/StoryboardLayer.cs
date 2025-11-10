namespace StorybrewCommon.Storyboarding
{
    public abstract class StoryboardLayer : StoryboardSegment
    {
        public override string Identifier { get; }

        protected internal StoryboardLayerCommandCoordinator CommandCoordinator { get; } = new StoryboardLayerCommandCoordinator();

        public StoryboardLayer(string identifier)
        {
            Identifier = identifier;
        }
    }
}
