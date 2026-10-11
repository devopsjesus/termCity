using TermCity.GodotApp;

namespace TermCity.Tests;

public class PauseAnimationTests
{
    [Theory]
    [InlineData(0, PauseActivity.PingPong)]
    [InlineData(6, PauseActivity.WalkingToGame)]
    [InlineData(10, PauseActivity.VideoGame)]
    [InlineData(16, PauseActivity.WalkingToMeditate)]
    [InlineData(20, PauseActivity.Meditating)]
    [InlineData(26, PauseActivity.LookingRight)]
    [InlineData(28, PauseActivity.LookingLeft)]
    [InlineData(30, PauseActivity.FacingViewer)]
    [InlineData(32, PauseActivity.ZoomingIn)]
    [InlineData(34, PauseActivity.Blinking)]
    [InlineData(46, PauseActivity.ZoomingOut)]
    [InlineData(48, PauseActivity.WalkingToTable)]
    [InlineData(56, PauseActivity.PingPong)]
    public void ScenesFollowTheRequestedLoop(double seconds, PauseActivity expected) =>
        Assert.Equal(expected, PauseAnimation.ActivityAt(seconds));

    [Fact]
    public void EveryFrameHasStableAsciiDimensionsAndLoopsExactly()
    {
        for (int tick = 0; tick < PauseAnimation.CycleSeconds * PauseAnimation.FramesPerSecond; tick++)
        {
            double seconds = tick / (double)PauseAnimation.FramesPerSecond;
            string frame = PauseAnimation.FrameAt(seconds);
            string[] lines = frame.Split('\n');
            Assert.Equal(PauseAnimation.Height, lines.Length);
            Assert.All(lines, line => Assert.Equal(PauseAnimation.Width, line.Length));
            Assert.All(frame, character => Assert.InRange((int)character, 0, 127));
            Assert.Equal(frame, PauseAnimation.FrameAt(seconds + PauseAnimation.CycleSeconds));
        }
    }

    [Fact]
    public void EyesCloseAndReopenAndZoomHasMultipleSizes()
    {
        Assert.Contains("( o )", PauseAnimation.FrameAt(34));
        Assert.Contains("-----", PauseAnimation.FrameAt(38));
        Assert.Contains("( o )", PauseAnimation.FrameAt(38.25));
        Assert.Equal(PauseAnimation.FrameAt(34), PauseAnimation.FrameAt(46));
        var zoomFrames = Enumerable.Range(0, 17).Select(frame => PauseAnimation.FrameAt(32 + frame / 8d)).Distinct();
        Assert.True(zoomFrames.Count() >= 12, "The two-second zoom must have many small steps, not three scene cuts.");
        for (double time = 34; time < 38; time += 0.125)
            Assert.DoesNotContain("-----", PauseAnimation.FrameAt(time).Split('\n')[4]);
    }

    [Theory]
    [InlineData(6, 10, 37, 14)]
    [InlineData(16, 20, 14, -30)]
    [InlineData(48, 56, -30, 37)]
    public void CharacterWalksContinuouslyToEachActivityAndBack(double start, double end, int from, int to)
    {
        Assert.Equal(from, PauseAnimation.CharacterXAt(start));
        Assert.Equal(to, PauseAnimation.CharacterXAt(end));
        Assert.Equal(from, PauseAnimation.CharacterXAt(start + 0.375));
        int direction = Math.Sign(to - from);
        for (double time = start; time + 0.125 <= end; time += 0.125)
        {
            int difference = PauseAnimation.CharacterXAt(time + 0.125) - PauseAnimation.CharacterXAt(time);
            Assert.InRange(difference * direction, 0, 2);
        }
        Assert.NotEqual(PauseAnimation.FrameAt(start + 1), PauseAnimation.FrameAt(start + 2));
    }

    [Fact]
    public void TableHalfStaysUprightAndBallReturnsToStationaryPlayer()
    {
        string[] first = PauseAnimation.FrameAt(0).Split('\n');
        string player = Body(first);
        for (double time = 0; time < 6; time += 0.125)
        {
            string[] lines = PauseAnimation.FrameAt(time).Split('\n');
            Assert.Equal(37, PauseAnimation.CharacterXAt(time));
            Assert.Equal(player, Body(lines));
            Assert.All(lines.Skip(3).Take(4), line => Assert.Equal('|', line[33]));
        }
        Assert.Equal(20, BallX(0));
        Assert.Equal(26, BallX(0.5));
        Assert.Equal(32, BallX(1));
        Assert.Equal(26, BallX(1.5));
        Assert.Equal(20, BallX(2));
        for (double time = 0; time < 4; time += 0.125)
            Assert.Equal(PauseAnimation.FrameAt(time), PauseAnimation.FrameAt(time + 2));

        static int BallX(double time) => PauseAnimation.FrameAt(time).Split('\n')
            .Where(line => line.Contains('*')).Single().IndexOf('*');
        static string Body(string[] lines) => $"{lines[6][13]}{lines[7][13]}{lines[8].Substring(12, 4)}";
    }

    [Fact]
    public void WaitingRoutineUsesBalancedPacingAndLingersOnActivities()
    {
        Assert.Equal(56, PauseAnimation.CycleSeconds);
        Assert.Equal(2, PauseAnimation.BeatSeconds);
        Assert.Equal(8, PauseAnimation.FramesPerSecond);
        Assert.Equal(PauseActivity.PingPong, PauseAnimation.ActivityAt(5.75));
        Assert.Equal(PauseActivity.VideoGame, PauseAnimation.ActivityAt(15.75));
        Assert.Equal(PauseActivity.Meditating, PauseAnimation.ActivityAt(25.75));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public void IdleActionsRepeatOnTheTwoSecondBeat(double start)
    {
        for (double offset = 0; offset < 2; offset += 0.125)
            Assert.Equal(PauseAnimation.FrameAt(start + offset), PauseAnimation.FrameAt(start + offset + 2));
    }

    [Fact]
    public void AllActivityTransitionsLandOnTheSharedBeat()
    {
        var previous = PauseAnimation.ActivityAt(0);
        for (double time = 0.125; time <= PauseAnimation.CycleSeconds; time += 0.125)
        {
            var activity = PauseAnimation.ActivityAt(time);
            if (activity != previous) Assert.Equal(0, time % PauseAnimation.BeatSeconds);
            previous = activity;
        }
    }

        [Theory]
        [InlineData(34, false)]
        [InlineData(37.875, false)]
        [InlineData(38, true)]
        [InlineData(38.25, false)]
        [InlineData(39.875, false)]
        [InlineData(40, true)]
        [InlineData(40.25, false)]
        [InlineData(41.875, false)]
        [InlineData(42, true)]
        [InlineData(42.25, false)]
        [InlineData(43.875, false)]
        [InlineData(44, true)]
        [InlineData(44.125, false)]
        [InlineData(44.25, false)]
        [InlineData(44.375, true)]
        [InlineData(44.5, false)]
        [InlineData(45.875, false)]
        public void CloseupUsesFourSecondWaitThenTwoSecondBlinksAndQuickDoubleBlink(double seconds, bool closed)
        {
            string eyeRow = PauseAnimation.FrameAt(seconds).Split('\n')[4];
            if (closed) Assert.Contains("-----", eyeRow);
            else Assert.Contains("( o )", eyeRow);
        }
    [Fact]
    public void LeftwardWalkLetsTheTableLeaveAndTheTelevisionEnter()
    {
        Assert.True(PauseAnimation.CharacterXAt(7) < PauseAnimation.CharacterXAt(6));
        Assert.Contains("<o", PauseAnimation.FrameAt(6));
        Assert.Contains("<o", PauseAnimation.FrameAt(16));
        Assert.DoesNotContain("+---------+", PauseAnimation.FrameAt(0));
        Assert.Contains("+---------+", PauseAnimation.FrameAt(10));
        Assert.DoesNotContain("--------------+", PauseAnimation.FrameAt(10));
        Assert.Contains("=========", PauseAnimation.FrameAt(20));
        Assert.DoesNotContain("+---------+", PauseAnimation.FrameAt(20));
    }

    [Fact]
    public void PaddleMovesWhileFeetStayStillAndGamerTapsThenShakesHisFist()
    {
        string[] paddleDown = PauseAnimation.FrameAt(1).Split('\n');
        string[] paddleUp = PauseAnimation.FrameAt(1.875).Split('\n');
        Assert.Equal(']', paddleDown[7][15]);
        Assert.Equal(']', paddleUp[6][15]);
        Assert.Equal(paddleDown[8].Substring(12, 4), paddleUp[8].Substring(12, 4));
        Assert.NotEqual(PauseAnimation.FrameAt(10), PauseAnimation.FrameAt(10.5));
        Assert.Contains("ONE MORE LEVEL", PauseAnimation.FrameAt(10));
        Assert.Contains("REALLY?!", PauseAnimation.FrameAt(14));
        Assert.NotEqual(PauseAnimation.FrameAt(14), PauseAnimation.FrameAt(15));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.25, false)]
    [InlineData(1, false)]
    [InlineData(1.5, false)]
    [InlineData(1.75, true)]
    [InlineData(1.875, true)]
    [InlineData(2, true)]
    [InlineData(2.25, false)]
    public void PaddleRisesForTheReturningBallAndDropsAfterTheHit(double seconds, bool raised)
    {
        string[] lines = PauseAnimation.FrameAt(seconds).Split('\n');
        Assert.Equal(raised ? ']' : ' ', lines[6][15]);
        Assert.Equal(raised ? ' ' : ']', lines[7][15]);
        if (raised)
        {
            int ball = lines.Single(line => line.Contains('*')).IndexOf('*');
            Assert.InRange(ball, 20, 23);
        }
    }

    [Fact]
    public void GamerAndRaisedFistStayClearOfTheTelevision()
    {
        foreach (double seconds in new[] { 10d, 10.5, 14, 15 })
        {
            string[] lines = PauseAnimation.FrameAt(seconds).Split('\n');
            int televisionRight = lines[3].LastIndexOf('+');
            int handsLeft = lines[5].IndexOf('o');
            if (seconds != 14) handsLeft = lines[6].IndexOfAny(['.', '-', 'o'], televisionRight + 1);
            Assert.True(handsLeft - televisionRight >= 2,
                "The closest hand/fist must leave a visible gap beside the television.");
            Assert.Equal(14, PauseAnimation.CharacterXAt(seconds));
        }
    }

    [Fact]
    public void MeditationShowsAHumanPoseWithoutOtherActivityEquipment()
    {
        string meditation = PauseAnimation.FrameAt(20);
        Assert.Contains("( -_- )", meditation);
        Assert.Contains("/| |\\", meditation);
        Assert.Contains("(_/___\\_)", meditation);
        Assert.Contains("===========", meditation);
        Assert.DoesNotContain('+', meditation);
        Assert.DoesNotContain("--------------", meditation);
        Assert.DoesNotContain("+---------+", PauseAnimation.FrameAt(0));
        Assert.DoesNotContain("===========", PauseAnimation.FrameAt(0));
        Assert.DoesNotContain("--------------", PauseAnimation.FrameAt(10));
        Assert.DoesNotContain("===========", PauseAnimation.FrameAt(10));
    }

    [Theory]
    [InlineData(0, PausePart.Table)]
    [InlineData(10, PausePart.Television)]
    [InlineData(20, PausePart.Mat)]
    public void FreeformStageKeepsStationaryEquipmentIsolated(double seconds, PausePart expected)
    {
        var visibleProps = PauseRig.FrameAt(seconds).Where(glyph =>
            glyph.Part is PausePart.Table or PausePart.Television or PausePart.Mat &&
            glyph.X + glyph.Text.Length * glyph.Scale / 2 >= 0 &&
            glyph.X - glyph.Text.Length * glyph.Scale / 2 < PauseAnimation.Width).ToArray();
        Assert.NotEmpty(visibleProps);
        Assert.All(visibleProps, glyph => Assert.Equal(expected, glyph.Part));
    }

    [Fact]
    public void SkeletonPaddleMeetsTheReturningBallAndLimbsMoveFreely()
    {
        var strike = PauseRig.FrameAt(2);
        var ball = Assert.Single(strike, glyph => glyph.Part == PausePart.Ball);
        var paddle = Assert.Single(strike, glyph => glyph.Part == PausePart.Paddle);
        Assert.Equal(ball.X, paddle.X);
        Assert.Equal(ball.Y, paddle.Y);
        Assert.True(Assert.Single(PauseRig.FrameAt(1), glyph => glyph.Part == PausePart.Paddle).Y > paddle.Y);
        var walking = PauseRig.FrameAt(7.1);
        Assert.Contains(walking, glyph => glyph.Part == PausePart.UpperArm && Math.Abs(glyph.Rotation) > 0.1);
        Assert.Contains(walking, glyph => glyph.X != Math.Round(glyph.X) || glyph.Y != Math.Round(glyph.Y));
        Assert.Equal(PauseRig.FrameAt(0).Where(glyph => glyph.Part == PausePart.Foot),
            PauseRig.FrameAt(1).Where(glyph => glyph.Part == PausePart.Foot));
    }

    [Fact]
    public void SkeletalGamerHasRoomForHisHandsAndMeditationHasCrossedLegs()
    {
        var gaming = PauseRig.FrameAt(14);
        double tvRight = gaming.Where(glyph => glyph.Part == PausePart.Television)
            .Max(glyph => glyph.X + (glyph.Text.Length - 1) / 2d);
        Assert.All(gaming.Where(glyph => glyph.Part == PausePart.Hand), hand => Assert.True(hand.X > tvRight + 1));
        var meditation = PauseRig.FrameAt(20);
        Assert.Equal("(-_-)", Assert.Single(meditation, glyph => glyph.Part == PausePart.Head).Text);
        Assert.Equal(2, meditation.Count(glyph => glyph.Part == PausePart.Thigh));
        Assert.Equal(2, meditation.Count(glyph => glyph.Part == PausePart.Shin));
        Assert.True(meditation.Where(glyph => glyph.Part == PausePart.Shin).All(glyph => Math.Abs(glyph.Rotation) > 0.3));
    }

    [Fact]
    public void FreeformZoomIsContinuousAndBlinksFollowTheExactSchedule()
    {
        double previousScale = 0;
        for (double seconds = 32; seconds <= 34; seconds += 1 / 30d)
        {
            var head = Assert.Single(PauseRig.FrameAt(seconds), glyph => glyph.Part == PausePart.Head);
            Assert.True(head.Scale >= previousScale);
            Assert.True(head.Scale - previousScale < 0.2 || previousScale == 0);
            previousScale = head.Scale;
        }
        foreach (double seconds in new[] { 38d, 40, 42, 44, 44.375 })
            Assert.Equal("(-_-)", Assert.Single(PauseRig.FrameAt(seconds), glyph => glyph.Part == PausePart.Head).Text);
        foreach (double seconds in new[] { 34d, 37.875, 38.25, 40.25, 42.25, 44.125, 44.5 })
            Assert.Equal("(o_o)", Assert.Single(PauseRig.FrameAt(seconds), glyph => glyph.Part == PausePart.Head).Text);
    }
}
