# Windows bridge regression checks

Run `dotnet run --project Operating_System_Specific/Windows/BridgeRegressionTests -c Release`
from the repository root. These checks use local sockets and exercise hello
gating, ordered forwarding, session replacement, disconnect cleanup, and held
key state without connecting to another computer or injecting test input.

Run SecureTransportTests and ModifierRegressionTests as described in the
[Windows guide](../README.md) for the remaining automated checks.
Live crossings and held-input recovery still require both computers.
