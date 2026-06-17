# LanEmulator - Architecture Refactoring Branch

## Overview

This branch contains architectural improvements to the LanEmulator codebase.

### Completed Changes

#### 1. Extract Models (Models.cs)
- **Problem**: All models were in `engine.cs`, violating SRP
- **Solution**: Created `LanEmulator.Core/Models.cs`

#### 2. Create IEngine Interface
- **Problem**: Engine was a God Object without abstractions
- **Solution**: Created `IEngine` interface

#### 3. Engine Implements IEngine
- Engine now explicitly implements IEngine

#### 4. Updated .gitignore
- Added rules to exclude binary files

## Next Steps

### Priority 1 (Critical)
- Split Engine into modules
- Implement Dependency Injection
- Add UDP encryption
- Implement proper NAT traversal
- Remove empty catch blocks

### Priority 2 (Important)
- Rewrite GUI using MVVM
- Replace Thread with Task.Run
- Remove blocking calls
- Add unit tests

---

**Status**: Refactoring in progress
**Date**: 2026-06-18