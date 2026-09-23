# Third-party notices

Airwave is licensed under AGPL-3.0-only; see `LICENSE.md`. Its capture helper
includes adapted process-loopback and synthetic-test foundations from Pulsar;
see the capture helper's README and license for provenance.

The plugin runtime payload includes these independently licensed components:

| Component | Version | License notice |
| --- | --- | --- |
| Concentus | 2.2.2 | [BSD license and Opus attribution](licenses/Concentus-LICENSE.txt) |
| NAudio.Core and NAudio.Wasapi | 3.1.0 | [MIT license](licenses/NAudio-LICENSE.txt) |
| System.Numerics.Tensors | 9.0.0 | [.NET MIT license](licenses/DotNet-LICENSE.txt) and [third-party notices](licenses/DotNet-THIRD-PARTY-NOTICES.txt) |
| System.Security.Cryptography.ProtectedData | 10.0.0 | [.NET MIT license](licenses/DotNet-LICENSE.txt) and [third-party notices](licenses/DotNet-THIRD-PARTY-NOTICES.txt) |
| .NET runtime and executable hosts | .NET 10; exact version in `licenses/runtime-versions.json` | Runtime MIT license and third-party notices in `licenses/DotNet-Runtime-LICENSE.TXT` and `licenses/DotNet-Runtime-THIRD-PARTY-NOTICES.TXT` |
| ASP.NET Core runtime | .NET 10; exact version in `licenses/runtime-versions.json` | MIT license and third-party notices in `licenses/AspNetCore-Runtime-LICENSE.TXT` and `licenses/AspNetCore-Runtime-THIRD-PARTY-NOTICES.TXT` |

The .NET and ASP.NET Core runtimes are bundled with the portable helpers.
Their version-specific notices are copied from the exact runtime packs during
the build. The standalone relay archive includes its required runtime and the
same notices; it does not include the audio codec or capture executable.

Concentus and the System.Numerics.Tensors notice files retain their runtime
package attribution. The NAudio notice retains the copyright attribution and
MIT license declared by both NAudio 3.1.0 packages. Bundled third-party binaries
retain their original publisher metadata and licenses.
