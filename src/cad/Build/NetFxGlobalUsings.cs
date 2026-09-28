// Bare Clamp/IsFinite/ThrowIfNull call sites resolve to CompatShims on every target framework, so the
// shared source has one semantic instead of a per-framework branch at each call site.
global using static CadTranslation.Compat.CompatShims;
#if NETFRAMEWORK
global using CadTranslation.Compat;
#endif
