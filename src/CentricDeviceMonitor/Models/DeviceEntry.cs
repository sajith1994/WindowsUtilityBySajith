using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Models;

public sealed class DeviceEntry : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _ipAddress = string.Empty;
    private string _hostname = "Not available";
    private string _macAddress = "Not available";
    private string _status = "Not checked";
    private long? _roundTripTime;
    private DateTime? _lastChecked;
    private string _lastError = string.Empty;
    private bool _isChecking;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetField(ref _ipAddress, value);
    }

    [JsonIgnore]
    public string Hostname
    {
        get => _hostname;
        set => SetField(ref _hostname, value);
    }

    [JsonIgnore]
    public string MacAddress
    {
        get => _macAddress;
        set => SetField(ref _macAddress, value);
    }

    [JsonIgnore]
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    [JsonIgnore]
    public long? RoundTripTime
    {
        get => _roundTripTime;
        set
        {
            if (SetField(ref _roundTripTime, value))
            {
                OnPropertyChanged(nameof(ResponseDisplay));
            }
        }
    }

    [JsonIgnore]
    public DateTime? LastChecked
    {
        get => _lastChecked;
        set
        {
            if (SetField(ref _lastChecked, value))
            {
                OnPropertyChanged(nameof(LastCheckedDisplay));
            }
        }
    }

    [JsonIgnore]
    public string LastError
    {
        get => _lastError;
        set => SetField(ref _lastError, value);
    }

    [JsonIgnore]
    public bool IsChecking
    {
        get => _isChecking;
        set => SetField(ref _isChecking, value);
    }

    [JsonIgnore]
    public string ResponseDisplay => RoundTripTime.HasValue ? $"{RoundTripTime.Value} ms" : "-";

    [JsonIgnore]
    public string LastCheckedDisplay => LastChecked.HasValue
        ? LastChecked.Value.ToString("dd MMM yyyy HH:mm:ss")
        : "Never";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
