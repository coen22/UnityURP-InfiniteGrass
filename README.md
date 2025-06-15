# UnityURP-InfiniteGrass
Fully Procedural and Dynamic Grass for Unity URP.

It meant to be a fast to implement grass system that doesn't need any baking or having any static environemnt.</br>
Just enable it, give it the LayerMask of the objects where you want it to be, and everything gets drawn procedurally.

### Preview Video
Tested on RTX 3060: https://youtu.be/NwVtPIxUuCY

### How to Use
Just drag the "InfiniteGrass" folder to your project then go to your URP settings and add the "GrassDataRendererFeature" to it.</br>
From there choose the LayerMask of your Terrain mesh.</br>
Assign the Material and the ComputeShader (Included in the folder).</br></br>
![image](https://github.com/user-attachments/assets/c673ac00-ec45-4300-847a-7854c105efff)

Next, in your scene, make an empty object and add the "InfiniteGrassRenderer" script to it.</br>
Assign the Grass Material (Included in the folder) and play with the settings until you get what you want.</br></br>
![image](https://github.com/user-attachments/assets/cd034441-e707-45ac-88bc-c103c21d3713)

# Features
### Fully Procedural:
You don't need to have a HeightMap or use the Unity Terrain, you can put grass on anything just by changing the LayerMask of it.</br>
Also it doesn't require generating a big buffer of positions of the whole world, it generates just the necessary amout of positions around the camera so the Memory isn't a big concern.</br></br>
![Image Sequence_002_0000](https://github.com/user-attachments/assets/1ef15340-b6bd-45e2-a17c-22448ebb8732)

### Frustum Culling and Smooth Density Falloff:
Grass density now fades out smoothly from the camera position to the draw
distance. Each cell uses a stable random threshold so blades disappear only
once as you move, preventing flickering in the distance and giving a natural
transition. Use the **Density Falloff Exponent** parameter on the
`InfiniteGrassRenderer` component to control how gradual the fade is.</br></br>
![image](https://github.com/user-attachments/assets/0ae48893-7149-47f1-a846-949183c8e9d9)

### Dynamic Color Modifier:
It allows you to modify the color of the grass blades using any object or texture you want.</br>
To make you own color modifier, create a new material from "InfiniteGrass/Modifiers/GrassColoringShader", give the material a texture and a color.</br>
Finally, add a quad mesh to your scene and apply the material to it, you can then place the object wherever you want with any scale or rotation like a Decal.</br>
There is no problem also in using Particle Systems with that shader like the waves in the preview video.</br></br>
![Image Sequence_003_0000](https://github.com/user-attachments/assets/c1d1bef9-d3d2-4689-b8f1-3ebd2f0f75ae)

### Dynamic Mask and Density:
Just like the color modifier, just make a material from "InfiniteGrass/Modifiers/GrassMaskShader" and apply it to a quad or any other mesh.</br>
Just note that the usual meaning of "mask" here isn't what it's used, White means the grass will fully be cutout, Black means full density.</br>
You can also instead of fully cutting out the grass make the density decrease by making the material "Opacity" property lower.</br>
(The Red Channel of the VertexColor of your meshes also occludes the grass).</br></br>
![Image Sequence_004_0000](https://github.com/user-attachments/assets/8e0fd3b1-f24f-44ed-994a-d8989242ac0d)

### Dynamic Slope:
By "Slope" I mean the inclination of the grass blade.</br>
It's just a simple modifier like others, just make a material and apply it to a quad, but this time the color will describe the direction where the grass blade will be directed to.</br>
Red controls how much the grass is inclined to the X axis (0: it will go to the -x, 1: it will go to the x, 0.5: it will stay upward).</br>
Green controls how much the grass is inclined to the Z axis (0: it will go to the -z, 1: it will go to the z, 0.5: it will stay upward).</br>
There is no need to use the blue channel cause the grass can't be inclined to the Y axis (it just mean it's upward).</br>
This is usefull if you want to make Custom Wind effects, Explosions, Stepping on grass ...</br>
There is an example of each of these in the Sample Scene.</br></br>
![Untitled-2](https://github.com/user-attachments/assets/17bacc32-a0c8-4479-a7a0-0e5ab7627c91)</br>
![Untitled-3](https://github.com/user-attachments/assets/2039ce7d-0d3f-44df-aef9-023f2bc67a9f)

### Wind System:
Wind from texture, similar to the "Dynamic Slope" but just applied to the whole grass field.</br></br>
![image](https://github.com/user-attachments/assets/fea2e411-ed77-45cb-87d9-c170cae28fe9)

### Stylized Billboard Grass:
The grass blades are always (atleast trying) to look to the camera from all angles.</br>
The material includes a lot of parameters to customize the look. Increasing
**Expand Distant Grass Width** helps the thin distant blades blend more smoothly
before they fade out.</br></br>
![image](https://github.com/user-attachments/assets/ca5d7ff4-063a-49a3-bebb-c8bc92162576)

## Performance Optimizations  <!-- NEW -->

| Tip                                                                                                                      | Why it helps                                                                                           |
|--------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------|
| **Update the height/mask/ color/ slope textures only when the camera has moved farther than `Texture Update Threshold`** | Skips the four raster passes on frames where nothing in view has changed.                              |
| **Dispatch the compute shader at half-rate (e.g. every second frame) on high-end GPUs**                                  | The grass position buffer often stays valid for two frames without artefacts, halving compute cost.    |
| **Quantise the camera-space centre to the grass cell size**                                                              | Ensures cache-friendly reuse of generated cells and avoids frequent buffer resets.                     |
| **Use `UNITY_VERTEX_OUTPUT_STEREO` only when XR is enabled**                                                             | Removes the extra instancing overhead for mono rendering.                                              |
| **Pack the height and mask into a single `R16G16` texture**                                                              | Reduces tile memory pressure compared with two separate resources.                                     |
| **Early-out in the fragment shader when the blade normal faces away from the light by more than 120 °**                  | Saves ALU on back-lit blades that will be alpha-faded anyway.                                          |
| **Switch distant blades to an unlit material variant once they cross the half draw-distance**                            | Removes per-pixel lighting for the majority of on-screen grass while keeping near blades fully shaded. |
| **Use `MaterialPropertyBlock` to push per-camera wind parameters**                                                       | Avoids creating material instances and keeps SRP batching intact.                                      |
| **Batch `GraphicsBuffer.CopyCounterValue` calls together**                                                               | Minimises command-buffer stalls caused by counter queries.                                             |
| **Enable Hi-Z culling in the compute shader (sample the depth pyramid before appending a position)**                     | Prevents blades that are fully occluded by terrain or large objects from ever being generated.         |
| **Strip shader variants that keep unused colour or slope channels**                                                      | Shrinks build size and slashes shader warm-up time at launch.                                          |

## Visual Optimizations

- Use an alpha value to fade out distant blades instead of fully cutting them out.
- Nearby grass should blend smoothly into the terrain texture.