# ![BlendShare](https://github.com/Tr1turbo/BlendShare/assets/162105654/b293cfd1-5cb8-4adb-9076-3fcee32c1913)

BlendShare is an Unity package designed for creators who need to share blendshapes without distributing the original FBX files.

This tool enables the extraction of blendshapes from FBX files and stores them in a custom asset format.
These assets can be easily shared and applied to compatible FBX files.


# Extractor
Click Tools -> BlendShare -> BlendShapes Extractor to open Blendshapes extractor

![Extractor](https://github.com/Tr1turbo/BlendShare/assets/162105654/add3a722-c04f-4773-bce7-6221b9fb994c)
- Origin FBX
    - The original FBX file.
- Source FBX
    - FBX file with added blendshapes.
- Default Asset Name
    - The default name for generated FBX and mesh
- Deformer ID
    - The deformer name of your blendshapes in FBX.
    - A deformer is like a group of blendshapes.
    - BlendShare will delete the old deformer with the same name if the user applies blendshapes again.
- Compare Method
    - Name
        - Compare by name. If there are new blendshapes in the original, BlendShare will extract them.
    - Index
        - Compare by index. If there are more blendshapes than the original, BlendShare will extract blendshapes from the tail end.
    - Custom
        - Toggle which blendshapes should be extracted individually.
