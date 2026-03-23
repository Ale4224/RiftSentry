using System.Windows.Input;
using RiftSentry.Commands;
using RiftSentry.Models;
using RiftSentry.SyncContracts;

namespace RiftSentry.ViewModels;

public sealed class EnemyChampionViewModel : ViewModelBase
{
    private string _rosterKey = "";
    private string _championName = "";
    private string _portraitPath = "";
    private bool _isDead;
    private bool _hasIonianBoots;
    private bool _manualCosmicInsightOverride;
    private bool _hasCosmicInsightFromApi;
    private bool _hasDetailedRunesFromApi;
    private string _lastSyncedAssetKey = "";

    public EnemyChampionViewModel()
    {
        SpellOne = new SpellSlotViewModel(() => TotalSummonerSpellHaste);
        SpellTwo = new SpellSlotViewModel(() => TotalSummonerSpellHaste);
        SpellOne.CooldownChanged += (startedAtUtc, endsAtUtc) => SpellCooldownChanged?.Invoke(SpellSlotNumber.One, startedAtUtc, endsAtUtc);
        SpellTwo.CooldownChanged += (startedAtUtc, endsAtUtc) => SpellCooldownChanged?.Invoke(SpellSlotNumber.Two, startedAtUtc, endsAtUtc);
        ToggleCosmicCommand = new RelayCommand(_ => ToggleCosmic());
    }

    public SpellSlotViewModel SpellOne { get; }
    public SpellSlotViewModel SpellTwo { get; }
    public ICommand ToggleCosmicCommand { get; }

    public event Action<SpellSlotNumber, DateTime?, DateTime?>? SpellCooldownChanged;

    public event Action<bool>? CosmicStateChanged;

    public string RosterKey
    {
        get => _rosterKey;
        private set => SetProperty(ref _rosterKey, value);
    }

    public string ChampionName
    {
        get => _championName;
        private set => SetProperty(ref _championName, value);
    }

    public string PortraitPath
    {
        get => _portraitPath;
        set => SetProperty(ref _portraitPath, value);
    }

    public bool IsDead
    {
        get => _isDead;
        private set => SetProperty(ref _isDead, value);
    }

    public bool HasIonianBoots
    {
        get => _hasIonianBoots;
        private set => SetProperty(ref _hasIonianBoots, value);
    }

    public bool HasCosmicInsightFromApi
    {
        get => _hasCosmicInsightFromApi;
        private set
        {
            if (Equals(_hasCosmicInsightFromApi, value)) return;
            _hasCosmicInsightFromApi = value;
            OnPropertyChanged(nameof(HasCosmicInsightFromApi));
            NotifyCosmicDerived();
        }
    }

    public bool HasDetailedRunesFromApi
    {
        get => _hasDetailedRunesFromApi;
        private set
        {
            if (Equals(_hasDetailedRunesFromApi, value)) return;
            _hasDetailedRunesFromApi = value;
            OnPropertyChanged(nameof(HasDetailedRunesFromApi));
            OnPropertyChanged(nameof(CosmicButtonTooltip));
        }
    }

    public bool ManualCosmicInsightOverride
    {
        get => _manualCosmicInsightOverride;
        set
        {
            if (Equals(_manualCosmicInsightOverride, value)) return;
            _manualCosmicInsightOverride = value;
            OnPropertyChanged(nameof(ManualCosmicInsightOverride));
            NotifyCosmicDerived();
        }
    }

    public bool IsCosmicActive => HasCosmicInsightFromApi || ManualCosmicInsightOverride;

    public string CosmicButtonTooltip
    {
        get
        {
            if (HasCosmicInsightFromApi)
                return "Cosmic Insight detected (+18)";
            if (ManualCosmicInsightOverride)
                return "Cosmic Insight forced on (+18)";
            if (!HasDetailedRunesFromApi)
                return "Rune details unavailable; tap C to force Cosmic Insight (+18)";
            return "Cosmic Insight not detected; tap C to force (+18)";
        }
    }

    public int TotalSummonerSpellHaste =>
        (HasIonianBoots ? 12 : 0) + (IsCosmicActive ? 18 : 0);

    public string LastSyncedAssetKey
    {
        get => _lastSyncedAssetKey;
        set => SetProperty(ref _lastSyncedAssetKey, value);
    }

    public void ApplySnapshot(EnemyPlayerSnapshot snap)
    {
        RosterKey = snap.RosterKey;
        ChampionName = snap.ChampionName;
        IsDead = snap.IsDead;
        HasIonianBoots = snap.HasIonianBoots;
        HasCosmicInsightFromApi = snap.HasCosmicInsightFromApi;
        HasDetailedRunesFromApi = snap.HasDetailedRunes;
    }

    private void NotifyCosmicDerived()
    {
        OnPropertyChanged(nameof(IsCosmicActive));
        OnPropertyChanged(nameof(CosmicButtonTooltip));
        OnPropertyChanged(nameof(TotalSummonerSpellHaste));
    }

    public void TickSpells()
    {
        SpellOne.Tick();
        SpellTwo.Tick();
    }

    public void ApplyCosmicState(bool enabled)
    {
        ManualCosmicInsightOverride = enabled;
    }

    public void ApplySpellCooldown(SpellSlotNumber slot, DateTime? startedAtUtc, DateTime? endsAtUtc)
    {
        GetSpellSlot(slot).ApplyCooldownState(startedAtUtc, endsAtUtc);
    }

    public SpellStateDto GetSpellState(SpellSlotNumber slot)
    {
        var spell = GetSpellSlot(slot);
        return new SpellStateDto(RosterKey, slot, spell.CooldownStartUtc, spell.CooldownEndUtc);
    }

    public CosmicStateDto GetCosmicState() => new(RosterKey, ManualCosmicInsightOverride);

    private SpellSlotViewModel GetSpellSlot(SpellSlotNumber slot) =>
        slot == SpellSlotNumber.One ? SpellOne : SpellTwo;

    private void ToggleCosmic()
    {
        ManualCosmicInsightOverride = !ManualCosmicInsightOverride;
        CosmicStateChanged?.Invoke(ManualCosmicInsightOverride);
    }
}
