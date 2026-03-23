using System.Windows.Input;
using RiftSentry.Commands;
using RiftSentry.Models;

namespace RiftSentry.ViewModels;

public sealed class SpellSlotViewModel : ViewModelBase
{
    private readonly Func<int> _getTotalHaste;
    private SummonerSpellDefinition? _definition;
    private DateTime? _cooldownStartUtc;
    private DateTime? _cooldownEndUtc;
    private double _totalDurationSeconds;
    private string _iconPath = "";
    private string _label = "";

    public SpellSlotViewModel(Func<int> getTotalHaste)
    {
        _getTotalHaste = getTotalHaste;
        ToggleCommand = new RelayCommand(_ => Toggle());
    }

    public ICommand ToggleCommand { get; }

    public event Action<DateTime?, DateTime?>? CooldownChanged;

    public string IconPath
    {
        get => _iconPath;
        private set => SetProperty(ref _iconPath, value);
    }

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public bool IsOnCooldown => _cooldownEndUtc.HasValue && DateTime.UtcNow < _cooldownEndUtc.Value;

    public double RemainingSeconds
    {
        get
        {
            if (!_cooldownEndUtc.HasValue) return 0;
            return Math.Max(0, (_cooldownEndUtc.Value - DateTime.UtcNow).TotalSeconds);
        }
    }

    public double ProgressFraction
    {
        get
        {
            if (!IsOnCooldown || _totalDurationSeconds <= 0) return 0;
            return Math.Clamp(RemainingSeconds / _totalDurationSeconds, 0, 1);
        }
    }

    public string RemainingText => IsOnCooldown ? $"{Math.Ceiling(RemainingSeconds):0}" : "";

    public DateTime? CooldownStartUtc => _cooldownStartUtc;

    public DateTime? CooldownEndUtc => _cooldownEndUtc;

    public void ApplyDefinition(SummonerSpellDefinition? def, string iconPath, string displayLabel)
    {
        _definition = def;
        IconPath = iconPath;
        Label = displayLabel;
        OnPropertyChanged(nameof(IsOnCooldown));
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(RemainingText));
    }

    public void Tick()
    {
        if (!_cooldownEndUtc.HasValue) return;
        if (DateTime.UtcNow >= _cooldownEndUtc.Value)
        {
            _cooldownStartUtc = null;
            _cooldownEndUtc = null;
            _totalDurationSeconds = 0;
        }

        RefreshCooldownProperties();
    }

    public void ApplyCooldownState(DateTime? startedAtUtc, DateTime? endsAtUtc)
    {
        if (startedAtUtc.HasValue && endsAtUtc.HasValue && endsAtUtc.Value > startedAtUtc.Value)
        {
            _cooldownStartUtc = startedAtUtc;
            _cooldownEndUtc = endsAtUtc;
            _totalDurationSeconds = (endsAtUtc.Value - startedAtUtc.Value).TotalSeconds;
        }
        else
        {
            _cooldownStartUtc = null;
            _cooldownEndUtc = null;
            _totalDurationSeconds = 0;
        }

        RefreshCooldownProperties();
    }

    private void Toggle()
    {
        if (_definition == null) return;
        if (IsOnCooldown)
        {
            ApplyCooldownState(null, null);
            CooldownChanged?.Invoke(null, null);
            return;
        }

        var h = _getTotalHaste();
        var baseCd = _definition.BaseCooldownSeconds;
        var final = baseCd * (100.0 / (100.0 + h));
        var startedAtUtc = DateTime.UtcNow;
        var endsAtUtc = startedAtUtc.AddSeconds(final);
        ApplyCooldownState(startedAtUtc, endsAtUtc);
        CooldownChanged?.Invoke(startedAtUtc, endsAtUtc);
    }

    private void RefreshCooldownProperties()
    {
        OnPropertyChanged(nameof(IsOnCooldown));
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(RemainingText));
    }
}
