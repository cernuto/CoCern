namespace CoCern.Core;

public interface IMachineRevocationListener
{
    void MachineRevoked(string machineId);
}
