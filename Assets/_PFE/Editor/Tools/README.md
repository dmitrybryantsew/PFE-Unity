# PFE Editor Tools

This directory contains editor tools for the PFE Unity port, created by the Data Librarian hat during Sprint 2 of the data port phase.

## Tools

### 1. XMLConverter.cs
**Purpose:** Convert AllData.as XML to Unity ScriptableObject assets

**Features:**
- Parses AllData.as XML structure (7,047 lines of embedded XML)
- Creates UnitDefinition assets from `<unit>` elements
- Creates WeaponDefinition assets from `<weapon>` elements
- Automatically creates subdirectories for organization
- Real-time progress tracking and error reporting

**Usage:**
1. Open Unity Editor
2. Select `PFE Tools > XML Converter` from menu
3. Click "Convert AllData.xml"
4. Assets generated to `Assets/_PFE/Data/Generated/`

**Reference:** docs/task3_data_architecture/01_xml_schemas.md

### 2. DataValidator.cs
**Purpose:** Validate data integrity across all ScriptableObject definitions

**Features:**
- Checks for missing or invalid IDs
- Validates enum values
- Detects duplicate IDs
- Checks for broken references
- Export validation reports to text files
- Filter results by text

**Usage:**
1. Open Unity Editor
2. Select `PFE Tools > Data Validator` from menu
3. Click "Validate All Data"
4. Review errors and warnings
5. Export report if needed

**Reference:** docs/task3_data_architecture/02_unity_mapping.md

### 3. SampleAssetCreator.cs
**Purpose:** Create sample assets for testing

**Features:**
- Creates minimal test data
- Verifies XMLConverter functionality
- Tests DataValidator validation
- Quick setup for GameDatabase testing

**Usage:**
1. Open Unity Editor
2. Select `PFE Tools > Create Sample Assets` from menu
3. Click "Create All Samples"
4. Assets generated to `Assets/_PFE/Data/Samples/`

## Data Architecture

### Definition Classes Referenced
- `UnitDefinition` - Unit/combatant data (~500 units)
- `WeaponDefinition` - Weapon data (~214 weapons)
- `ItemDefinition` - Item data (~500 items)
- `AmmoDefinition` - Ammunition data (~50 types)
- `PerkDefinition` - Perk data (~80 perks)
- `EffectDefinition` - Effect data (~50 effects)

### Source Data
- **File:** `pfe/scripts/fe/AllData.as`
- **Format:** Inline XML in ActionScript 3
- **Size:** ~7,047 lines
- **Structure:** Nested `<all>` root with `<unit>`, `<weapon>`, etc.

## Success Criteria

Sprint 2 delivers:
- [x] XMLConverter.cs - Parses AllData.as XML
- [x] DataValidator.cs - Validates data integrity
- [x] SampleAssetCreator.cs - Creates test assets
- [x] Editor menu integration
- [x] Build verification (syntax validated, braces balanced)

Next Sprint:
- Full data conversion of all 500+ units
- Full data conversion of all 214 weapons
- Integration with GameDatabase registries
- Automated validation pipeline

## Technical Notes

### XML Parsing
- Uses `System.Xml.XmlDocument` for parsing
- Extracts XML from ActionScript literal (finds `<all>`...`</all>`)
- Handles parent-child template inheritance
- Converts pixel units to Unity units (100px = 1 unit)

### Type Safety
- All enums validated at runtime
- String IDs checked for null/empty
- Parse operations have fallback defaults
- Duplicate ID detection across all asset types

### Performance
- Assets created directly via `AssetDatabase.CreateAsset`
- No runtime instantiation overhead
- ScriptableObjects loaded on-demand by GameDatabase
- Target: < 1 second load time for all data

## Author
Data Librarian Hat - Sprint 2 Implementation
Date: 2026-01-27
