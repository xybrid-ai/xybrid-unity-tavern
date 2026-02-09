# NPC Setup Checklist
Repeat for each NPC. Same process as Greta.

## Per NPC:
1. Import FBX into Assets/Characters/
2. Select FBX → Rig tab → Humanoid → Apply
3. Select FBX → Materials tab → Extract Textures + Extract Materials → Apply
4. Create Empty GameObject → name it (see below)
5. Drag the model as a child
6. Select parent → Layer = NPC (yes, change children)
7. Add Component → Capsule Collider (Center Y=0.9, Radius=0.5, Height=1.8)
8. Add Component → NPCIdentity (fill in name, description, personality)
9. Drag the matching Dialogue Data ScriptableObject into the Dialogue Data slot
10. Select the model child → Add Component → Animator → assign NPC_Animator controller + avatar
11. Position in the tavern

## NPC Placements:
- NPC_Aldric  → at a corner table
- NPC_Elara   → near the fireplace 
- NPC_Tomlin  → at the bar (near Greta)
- NPC_Sable   → dark corner, away from others
