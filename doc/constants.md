# Tuning constants

Every number ABChapterize's detection is calibrated with, and what each one means.
`--set:<class>.<constant>=<value>` overrides one for a run; see
[the manual](manual.md#tuning-constants) for the option itself, what it is for, and the
warnings that come with it.

**Read the warning in the manual before changing anything here.** These values are not
arbitrary: nearly all of them were measured against real audiobooks, and the reasoning behind
each one lives in its own doc comment in the source (and, where the evidence is long, in the
`notes/` tree beside it). Changing one without reading that is how a book quietly loses
chapters.

A constant that is *derived* from others — `RescanShiftSeconds` is half
`WhisperChunkSeconds`, for instance — is not listed and cannot be set on its own. It follows
whatever its inputs are set to.

*This file is generated from the source's own documentation; `ConstantsDocTests` fails the
test suite when it no longer matches.*

## DetectionTuning

Chapter detection: window sizes, thresholds, retry budgets and mark placement.

| Constant | Default | Meaning |
| --- | --- | --- |
| `DefaultSilenceNoiseDb` | `-35` | Level below which ffmpeg's silencedetect counts audio as silence, in dBFS - the default of --noise-floor, and the value SilenceThresholdProbe's automatic mode keeps unless a master's own levels argue against it. |
| `NoiseProbeExcerpts` | `8` | How many excerpts SilenceThresholdProbe decodes to judge a file's levels, spread evenly between the 5% and 95% marks of its play time - a book's opening label jingle and closing credits are not what its body sounds like. |
| `NoiseProbeExcerptSeconds` | `20` | Length of one NoiseProbeExcerpts excerpt, in seconds. |
| `NoiseProbeFrameSeconds` | `0.05` | Length of one RMS frame in the level histogram. |
| `NoiseProbeFloorPercentile` | `5` | Percentile of the frame levels taken for the master's room tone. |
| `NoiseProbeSpeechPercentile` | `75` | Percentile of the frame levels taken for "sustained speech" - a level continuous narration reliably exceeds. |
| `NoiseFloorHeadroomDb` | `14` | How far above the measured room tone the silence threshold must sit, in dB. |
| `SpeechHeadroomDb` | `8` | How far below sustained speech the silence threshold must sit, in dB. |
| `MinAutoSilenceNoiseDb` | `-60` | The range an automatically chosen silence threshold is confined to, whatever the measurement says. |
| `MaxAutoSilenceNoiseDb` | `-20` | The upper end of that same range; see MinAutoSilenceNoiseDb, whose description this constant shares. |
| `MinStoredSilenceSeconds` | `0.5` | The shortest silence Analyze keeps (see the allSilences/silences split in DetectAsync), regardless of --min-silence-length. |
| `WindowEndSnapSearchSeconds` | `5.0` | How far past a Probe window's natural end PlanWindowEnd looks for a seam when that end has no next window to share a border with: the nearest silence - or VAD non-speech region, where the pre-pass ran - mid-point within this range becomes the window's end, so even a stand-alone window stops at a word-safe cut (a mid-word tail is exactly what makes Whisper garble a window's final phrase). |
| `PhraseLatestStartSeconds` | `5.0` | Without a VAD pre-pass, the phrase must start within this many seconds after the silence that triggered its probe (or a closer anchor silence still inside the window) to count as a real announcement rather than an in-text mention. |
| `ExpectedAnnouncementSeconds` | `22.0` | How far past the point where Probe's primary scan expects an announcement its probe window reaches - after the silence for a plain pause, after the music for a jingle (see BuildCandidates). |
| `SilenceLeadInSeconds` | `3.0` | How much of the silence before an expected announcement a probe window opens with, so Whisper has a run-up rather than starting hard on the first syllable. |
| `SandwichedAnnouncementSeconds` | `3.5` | How much speech may sit between a sub-threshold pause and the candidate pause behind it for SandwichedSilences to read the two as bracketing an announcement and promote the first to a candidate of its own. |
| `FidelityExcerpts` | `8` | How many excerpts of a file are measured to judge how much high frequency its speech has kept, which is the one reading deciding whether the book may be denoised at all. |
| `FidelityExcerptSeconds` | `30.0` | How much audio each of those excerpts covers. |
| `JingleLeadInSeconds` | `8.0` | The same run-up for a jingle candidate, taken from inside the music, and deliberately longer: the point it is measured back from is a VAD speech onset rather than a silencedetect edge, so it carries the detector's own latency plus whatever timeline drift survives Analyze's resync (see PcmResyncToleranceSeconds - seconds of it were measured across the corpus before that fix). |
| `PhraseMarginSeconds` | `5.0` | Flat margin added to a measured jingle length so the phrase after the jingle still fits into the probe window. |
| `RecoveryLeadInTrimSeconds` | `2.0` | How much of its lead-in a recovery pass's window gives up against the primary scan's (SilenceLeadInSeconds, JingleLeadInSeconds), and RecoveryReachTrimSeconds how much of its reach past the expectation. |
| `RecoveryReachTrimSeconds` | `5.0` | How much of its reach past the expected announcement a recovery pass's window gives up against ExpectedAnnouncementSeconds; see RecoveryLeadInTrimSeconds for why either is trimmed at all. |
| `JingleWalkFallbackLeadSeconds` | `0.5` | The fallback lead ComputeMarkBeforeJingle's step 5 backs off by when its backward walk runs out of VAD data before finding the previous chapter's real trailing narration - typically a jingle at the very start of the file, before chapter 1. |
| `DefaultMarkLeadSeconds` | `0.35` | Default for MarkLeadSeconds (--mark-lead): without --mark-before-jingle, the mark goes this many seconds before the detected phrase, whatever precedes it - no silence/jingle anchor is consulted for the timestamp at all, only for the --min-silence-length auto threshold and the per-file jingle statistics --summary reports. |
| `JinglePhraseMatchToleranceSeconds` | `0.5` | Slack when matching a VAD non-speech region (the jingle) to a Whisper phrase. |
| `LeadingSilenceStartToleranceSeconds` | `1.5` | How far a candidate LeadingSilence may start after its VAD non-speech region's own start and still count as leading it, rather than being an unrelated silence deep inside a long region (see that method's remarks). |
| `PreJingleSpeechToleranceSeconds` | `0.5` | How far past a jingle's own musical start a VAD speech blip swallowed into its region must begin before ResolveDefaultPhraseOnset will take it for the announcement rather than for the previous chapter's trailing words. |
| `JingleGlueMaxSeconds` | `3.0` | Longest stretch of VAD-speech "glue" the anchor-time jingle edge adjustment (see AdjustJingleRegion) steps across at the jingle's leading edge - both when trimming trailing-narration blips off a merged region's front and when bridging backward across an untranscribed music vocal to an earlier region the jingle was split into. |
| `SmearedPhraseMinOverlapSeconds` | `2.0` | Minimum overlap between a VAD non-speech region and the matched phrase's transcript-segment span for the smeared-phrase rescue (see FindSmearedJingleRegion) to accept that region as the jingle. |
| `SegmentLeadTrimToleranceSeconds` | `0.5` | Slack when deciding a Whisper segment starts with a stored silence or VAD non-speech region (see TrimLeadingNonSpeech). |
| `MinJingleObservationSeconds` | `2.0` | The shortest span this codebase treats as "plausibly a real jingle", used three ways. |
| `JingleFirstMinPerHour` | `1.0` | How much music a file must have, per hour of play time, before Probe reads its jingles first and its pauses only where the chapter sequence still wants one (see JingleFirstScan). |
| `MergeShortSpeechGapSeconds` | `1.0` | With the VAD pre-pass, a "speech" segment shorter than this between two non-speech regions does not end the surrounding jingle - the regions are merged and the blip treated as VAD noise. |
| `TransientSpeechFloorSeconds` | `0.4` | The speech-duration floor AdvancePastNonSpeech uses to tell a genuine spoken onset from a jingle's musical/vocal transients (or a Whisper hallucination inside one) that VAD still calls "speech". |
| `AnnouncementLeadInSeconds` | `0.85` | Non-speech an announcement must have in front of it before AnnouncementIsolation accepts it: the pause (or jingle) separating it from the previous section's narration. |
| `AnnouncementLeadOutSeconds` | `0.3` | Non-speech an announcement must have behind it where a wording's $ asks for one, and what a bare number is held to on top of its lead-in (Both). |
| `OnsetSegmentToleranceSeconds` | `0.1` | How far past a VAD speech segment's end an announcement onset may still be counted as belonging to it (Measure). |
| `MarkInsideSpeechSeconds` | `0.5` | How far a finished mark may sit inside a VAD speech segment before the refinement that put it there is disbelieved (DepthInsideSpeech, applied in KeepOutOfSpeech). |
| `MinPlausibleSpeechCharsPerSecond` | `3.0` | Minimum letters-plus-digits per second a transcript segment must average for JingleGeometry's corroboration checks (IsGenuineSpeech) to accept it as evidence that a VAD blip is real narration rather than jingle music. |
| `MaxPaceScrutinizedBlipSeconds` | `2.0` | Ceiling on a VAD speech blip's duration for MinPlausibleSpeechCharsPerSecond's pace check to apply at all: only a blip this short could plausibly be the brief transient that check exists to unmask - the premise behind TransientSpeechFloorSeconds, from the other side. |
| `PreciseMarkCheckWindowSeconds` | `4.0` | Length of the decode precise marking transcribes to check whether a mark's chapter phrase is really the first thing heard there (see RefinePreciseMarkAsync). |
| `PreciseMarkLeadInSeconds` | `0.1` | Real audio lead-in precise marking decodes before every position it checks, widening the window backward rather than shifting it so no PreciseMarkCheckWindowSeconds of fresh audio is lost off the tail. |
| `MarkLoudnessWindowSeconds` | `0.25` | How much audio MeasureDbfsAsync averages when reporting a finished mark's level under --verbose. |
| `PreciseMarkFixedStepSeconds` | `0.1` | The finest granularity precise marking probes at: the resolution both of its bisections - FindOnsetEdgeAsync and FindPhraseSurvivalEdgeAsync - narrow their bracket down to, and the step VerifyMarkBeforeJingleAsync's backward scan advances by. |
| `PlateauWalkLimitSeconds` | `60` | How far FindOnsetEdgeAsync's plateau walk may run from the position it set out from, counting resumes. |
| `PreciseMarkPlateauResumeLimit` | `2` | How many times FindOnsetEdgeAsync may restart its walk after finding the plateau resuming past an edge (see PreciseMarkPlateauProbesSeconds). |
| `PreciseMarkMinSurvivalSeconds` | `6.0` | The shortest stretch of audio PhraseSurvivesFromAsync will put in front of Whisper, however little is left between the probe position and the search's end anchor. |
| `WhisperChunkSeconds` | `30.0` | The stride whisper.cpp decodes in: it converts audio to a mel spectrogram of exactly this length at a time, so a window at or above it is transcribed as several passes whose results are stitched together, while a shorter one is a single pass over the whole thing. |
| `PreciseMarkQuietSnapRadiusSeconds` | `0.15` | How far before a confirmed or left-as-is mark SnapToQuietestPointAsync may search for a quieter point to move it to. |
| `PreciseMarkQuietWindowSeconds` | `0.01` | Width of the sliding RMS window SnapToQuietestPointAsync scans across PreciseMarkQuietSnapRadiusSeconds. |
| `PreciseMarkQuietSnapMinImprovementDb` | `6.0` | Minimum power-ratio improvement, in dB, a backward candidate within PreciseMarkQuietSnapRadiusSeconds must offer before SnapToQuietestPointAsync nudges the mark to it. |
| `PreciseMarkSilenceAnchorSeconds` | `1.0` | How far behind a refined onset PrecedingSilenceEnd will look for the silence AnchorOnsetToSoundAsync scans forward from, i.e. |
| `PreciseMarkOnsetFloorDb` | `25` | How far below the loudest thing in its window AnchorOnsetToSoundAsync still counts audio as the pause rather than as the announcement. |
| `PreciseMarkOnsetSustainSeconds` | `0.05` | How long audio must stay above PreciseMarkOnsetFloorDb before AnchorOnsetToSoundAsync accepts it as the announcement beginning rather than as a click inside the pause. |
| `SequenceRestartRunLength` | `3` | How many announcements must be rejected as below the sequence, their own numbers ascending, before SequenceRestartSkips reports the file as one whose chapter numbering restarts (see NoteOutOfSequence). |
| `AdaptiveTightenFactor` | `0.75` | With --min-silence-length auto, the Probe probing threshold is this factor times a mark's anchor silence length, i.e. |
| `AdaptiveSilenceFloorSeconds` | `0.8` | How short a chapter break --min-silence-length auto may end up believing in (ProposeThreshold), independent of the length the run starts at. |
| `SuspectGapMinMissing` | `3` | How many chapters a single announcement may leave missing before its number is treated as suspect and re-read (SuspectNumberMender). |
| `RefinedNumberVoteMinimum` | `3` | How many numbered readings a mark refinement's own probes must yield between them before their verdict may overrule the detecting window's (RefinedNumberVote). |
| `MaxSequenceRepairsPerFile` | `8` | How many outliers RepairSequenceOutliersAsync may spend audio re-reads on in one file. |
| `MaxUnnumberedMendsPerRegion` | `8` | How many unreadable-number re-reads (ReadUnnumberedAsync) one RegionProber region may run. |
| `SubFloorSweepBandCount` | `5` | How many sub-floor silence bands Re-probe sweeps through before giving a gap up to Scan, and how wide each band is (see SubFloorSweepBands). |
| `SubFloorSweepBandSeconds` | `0.1` | Width of one SubFloorSweepBandCount band, in seconds. |
| `SubFloorSweepBudgetFraction` | `0.75` | How much of Scan's own cost the sub-floor sweep may spend on a gap before giving it up (SweepSubFloorSilencesAsync), measured in WhisperChunkSeconds decode windows on both sides. |
| `GapChunkSeconds` | `600` | Chunk length in seconds for full transcription of gap regions. |
| `GapChunkOverlapSeconds` | `10` | Overlap between gap transcription chunks so no phrase is cut in half. |
| `ScanSeamSearchSeconds` | `30` | How far around a Scan chunk's natural border the seam search reaches in each direction: the border snaps to the nearest silence - or VAD non-speech region, where the pre-pass ran - mid-point in range, and the next chunk starts exactly there, with nothing decoded twice. |
| `ScanBridgeSeconds` | `15` | At a snapped (overlap-free) Scan seam, segments of the previous chunk ending within this many seconds before it are carried into the next chunk's phrase matching, so an announcement straddling the seam - the narrator pausing right where the seam silence sits, e.g. |
| `LowConfidenceThreshold` | `0.5` | Whisper segment probability below which a chapter detection is flagged as low-confidence rather than silently trusted: the point below which Whisper itself is, on average, more unsure than sure about the words it heard. |
| `MaxSettledWindowSkip` | `10` | How many consecutive candidates one confident mark may settle in SkipSettledWindows. |
| `MaxCustomMarksPerFile` | `100` | The most --custom marks one file may produce before the rest are dropped. |
| `NamedMarkDedupeSeconds` | `10` | How close two matches of the same repeatable phrase must be to count as the same announcement heard twice rather than two announcements. |
| `AutoLanguageProbabilityThreshold` | `0.6` | Whisper language-detection probability at or above which a single probe settles the file's language outright, with --lang auto. |
| `AutoLanguageCandidateNoiseThreshold` | `0.05` | With a --lang candidate list, the probability below which the vote's winner is treated as noise and the first-named candidate is used instead. |
| `AutoLanguageProbeAttempts` | `5` | How many language-detection samples one file may take before the vote decides it. |
| `AutoLanguageProbeSeconds` | `30` | Length of one language-detection sample. |
| `AutoLanguageExistingMarkOffsetSeconds` | `20` | How far past an existing chapter mark a language-detection sample starts, on the --verify and resume paths. |
| `MinLeadingGapSeconds` | `10` | Minimum length of the leading region (file start to the first detected chapter) for Scan to transcribe it in search of earlier chapters when the first detection is not chapter 1. |
| `VerifyMarginBeforeSeconds` | `10` | How far before a pre-existing chapter mark's own timestamp --verify starts probing - the mark may sit slightly after the phrase actually started. |
| `VerifyWindowSeconds` | `60` | Total length of the --verify probe window, starting VerifyMarginBeforeSeconds before the mark. |
| `VerifyFixMinShiftSeconds` | `0.25` | How far off a confirmed mark has to be before --verify --fix bothers moving it. |
| `VerifyFixMaxShiftSeconds` | `30` | The largest correction --verify --fix will apply. |
| `VerifyRereadAttempts` | `3` | How many differently-framed re-reads a --verify mark gets on the --upgrade-model recognizer once the first pass and the gap retry have both failed to find its number. |
| `VerifyRereadLeadSeconds` | `1.5` | How far before the mark the first reframed --verify re-read starts. |
| `VerifyRereadLeadStepSeconds` | `3.5` | How much further back each further reframed --verify re-read starts, spreading the ladder across the lead-in band rather than jittering inside one spot: a mark being verified need not be this tool's own, so how far it sits from the phrase is not known to within the tens of milliseconds a jitter would step by. |
| `VerifyRereadWindowSeconds` | `24` | Length of each reframed --verify re-read. |
| `GapRetryThresholdSeconds` | `3.0` | Minimum length for a gap between transcribed segments (or before the first/after the last) to be worth a focused re-transcription - and, for Scan's version of the same fallback (see ScanGapRetriesAsync), for a silence or VAD non-speech region overlapping that gap to count as "plausibly the real jingle/scene transition" rather than an in-narration pause. |
| `GapRetryPaddingSeconds` | `2.0` | Context padding added to each side of a gap before re-transcribing it, so the phrase is not cut off if it starts or ends right at the boundary. |
| `UnnumberedRetryLeadSeconds` | `8.0` | How far before the phrase a Scan "heard it, could not number it" retry starts its decode. |
| `UnnumberedRetryWindowSeconds` | `45.0` | Length of a Scan "heard it, could not number it" retry decode, chosen to match the framing that produced a readable number in the case UnnumberedRetryLeadSeconds documents, rather than the short sub-chunks GapRetryChunkSeconds uses - those recover audio Whisper skipped, which is the opposite problem and wants the opposite window. |
| `MaxUnnumberedRetriesPerChunk` | `3` | How many unreadable-number retries one Scan chunk may run. |
| `GapRetryChunkSeconds` | `8.0` | Length of each sub-chunk a padded gap is scanned in, rather than re-transcribing it in one call. |
| `GapRetryChunkOverlapSeconds` | `2.0` | Overlap between consecutive gap-retry sub-chunks, so a phrase straddling a chunk boundary is still fully contained in at least one of them. |

## VadSegmenter

Turning the voice-activity detector's per-frame probabilities into speech segments.

| Constant | Default | Meaning |
| --- | --- | --- |
| `Threshold` | `0.6f` | Frame speech probability at/above this counts as speech. |
| `MinSpeechSeconds` | `0.25` | A candidate speech run must persist at least this long to be kept (Silero's own default for min_speech_duration_ms). |
| `MinSilenceSeconds` | `0.1` | Non-speech must persist at least this long to end a speech run (Silero's own default for min_silence_duration_ms). |

## SileroVadDetector

How the voice-activity pre-pass is scheduled over a long file.

| Constant | Default | Meaning |
| --- | --- | --- |
| `BlockSeconds` | `600` | Audio per block, before its warm-up prefix. |
| `WarmupSeconds` | `60` | Audio prepended to each block purely to converge its recurrent state before the block's own frames start counting. |

## AudioFidelity

The measurement that decides whether a file is dull enough to be worth denoising.

| Constant | Default | Meaning |
| --- | --- | --- |
| `Threshold` | `0.02` | Below this ratio a file may be denoised. |

## AbsRetryPolicy

How long a run keeps trying to reach an Audiobookshelf server that is not answering
(`--abs-retry`), and which failures are worth trying again at all.

| Constant | Default | Meaning |
| --- | --- | --- |
| `RetryPauseSeconds` | `60.0` | How long to wait between attempts, in seconds. Long on purpose: what this exists to survive is a server that is down rather than one that is busy, and something restarting is not back a second later. Retrying quickly would spend the whole budget while the server was still coming up. |
