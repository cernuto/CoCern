# Native file scope producer

The hub exposes `Task<VerifiedNativeFileScope?> CallGuard.ResolveNativeFileScopeAsync(RelayCall call)` to protected file consumers. The six-string domain record is `VerifiedNativeFileScope(ParticipantId, SessionId, RepositoryId, MachineScope, CanonicalWorkingCopyRoot, NativeGeneration)`.

The accessor freshly verifies the caller's session handle and makes one private control-directory read. That read requires the authenticated enrolled-machine identity, matching principal/repository/token, the exact current native session and generation, and no closing fence. It uses the existing native-join registry, with no payload cache or second registry. Ordinary relay calls do not perform this lookup.

`MachineScope` comes only from the private machine claim created by machine-credential authentication. Configured local tokens and OAuth mappings have no machine scope. Labels, mutable bindings, principal prefixes and public tool arguments never supply it. The optional field survives Orleans surrogates and originating-caller effects; old absent fields remain absent.

The adapter may include optional `nativeJoin.protectedRoot` on a fresh private join, after `ProtectedCheckoutRoot.Prove`. The hub validates the drive-letter representation and bounds it at 32,767 UTF-16 units, removes the raw join before tracing, and retains the admitted workspace/root only in the private native registry. The participant hash and frozen five-field legacy join remain unchanged. Public calls cannot populate the corresponding hub-only fields. An existing generation is never upgraded by a replay carrying new proof.

Missing machine provenance or protected-root proof yields no protected scope, including all legacy joins. General relay access and native/UNC binding behavior remain unchanged. The root is an authenticated adapter assertion, not a physical check performed on the remote PC by the hub. The adapter bridge must merge together with this hub support, or after it. Hooks and activation are not enabled by this change.

The scope is a point-in-time result. File consumers must resolve it again at use and revalidate the physical checkout identity or hold appropriate physical handles. This producer alone does not prevent a later link retarget, rename, replacement generation or session closure.

Two small security tests cover private-session/machine/repository isolation and closing, plus legacy missing provenance, surrogate compatibility and effect propagation. They are source-only here; execution waits for the single related pre-deploy selection under AGENTS.md.
