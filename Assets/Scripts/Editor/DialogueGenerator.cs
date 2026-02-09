#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Run this once from the menu to generate all NPC dialogue assets.
/// Tools → Generate NPC Dialogues
/// </summary>
public class DialogueGenerator : MonoBehaviour
{
    [MenuItem("Tools/Generate NPC Dialogues")]
    public static void GenerateAll()
    {
        if (!AssetDatabase.IsValidFolder("Assets/DialogueData"))
            AssetDatabase.CreateFolder("Assets", "DialogueData");

        CreateGretaDialogue();
        CreateJiroDialogue();
        CreateWuDialogue();
        CreateSharaDialogue();
        CreateVarienDialogue();
        CreateSableDialogue();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("All NPC dialogues generated in Assets/DialogueData/");
    }

    static DialogueData CreateDialogue(string filename, string greeting, DialogueExchange[] exchanges)
    {
        DialogueData data = ScriptableObject.CreateInstance<DialogueData>();
        data.greeting = greeting;
        data.exchanges = exchanges;
        AssetDatabase.CreateAsset(data, $"Assets/DialogueData/{filename}.asset");
        return data;
    }

    static void CreateGretaDialogue()
    {
        CreateDialogue("Greta_Dialogue",
            "Welcome to the Rusty Flagon! What'll it be, traveler?",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What's good tonight?",
                        "Nice place. Is it yours?",
                        "Just looking around."
                    },
                    npcResponses = new string[]
                    {
                        "The stew's fresh... well, fresh-ish. Don't ask about the meat. Papa made it, so I have to say it's good.",
                        "Mine and my father's. He built this place with his bare hands. Now he mostly shuffles barrels and mutters about the old days. That's him behind me — don't mind the grumbling.",
                        "Look all you like. Just don't bother the hooded one in the corner. Last person who did... well, they left in a hurry."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What's the deal with that group at the table?",
                        "Any rumors lately?"
                    },
                    npcResponses = new string[]
                    {
                        "The old man is Master Wu. Comes in every week with his students. Orders one tea, sits for hours. The loud girl keeps the place lively though, so I don't mind.",
                        "Word is the old mine's been glowing at night. Folks are spooked. Master Wu just smiled when he heard — said something about the mountain breathing. Whatever that means."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "Your father seems like a character.",
                        "I should get going."
                    },
                    npcResponses = new string[]
                    {
                        "Ha! That's one word for it. He won't admit it, but he loves having people here. Keeps rearranging the bottles so he has an excuse to stay busy.",
                        "Take care out there. And come back thirsty!"
                    }
                }
            }
        );
    }

    static void CreateJiroDialogue()
    {
        CreateDialogue("Jiro_Dialogue",
            "*shuffles a crate without looking up* ...Hmm? Oh. Customer. Greta handles the drinks. I handle... everything else.",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "You built this place?",
                        "Need any help with those crates?",
                        "Your daughter seems to run things well."
                    },
                    npcResponses = new string[]
                    {
                        "Every beam. Every nail. Took me three summers. People said a tavern this far out would never last. Forty years later, here I am. Still fixing the same leaky roof.",
                        "*suspicious look* ...You're not after a job, are you? Last helper I had rearranged my whole cellar. Took me weeks to find the good mead.",
                        "She does. Better than I ever did, not that I'd tell her that. Girl's got her mother's head for people and my stubbornness. Dangerous combination."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What do you think of the regulars?",
                        "I'll let you get back to work."
                    },
                    npcResponses = new string[]
                    {
                        "The old master is good people. Quiet. Respectful. His loud student tips well. The other one stares too much. And that hooded one... *lowers voice* ...they never order anything. Just sits there. Bad for business, but Greta says leave it alone.",
                        "Work doesn't do itself. Unlike some people around here. *glances at the table group* ...Four hours on one pot of tea."
                    }
                }
            }
        );
    }

    static void CreateWuDialogue()
    {
        CreateDialogue("Wu_Dialogue",
            "*opens one eye slowly* ...You did not come to this table by accident.",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "I'm just exploring the tavern.",
                        "Are you really a martial arts master?",
                        "Your student over there is very... energetic."
                    },
                    npcResponses = new string[]
                    {
                        "There is no such thing as 'just' anything. Every step carries intention, even the ones we take without thinking. Especially those.",
                        "A master is just a student who kept showing up. I have practiced for sixty years. I still learn something new every morning. Usually that my knees hurt.",
                        "Shara has fire. Fire is useful. Fire is also dangerous. The art is not in having fire — it is in knowing when to let it burn and when to let it rest. She is still learning the second part."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What about your other student?",
                        "Can you teach me something?"
                    },
                    npcResponses = new string[]
                    {
                        "Varien watches. Varien listens. Varien doubts himself too much. He thinks Shara is stronger because she is louder. He has not yet realized that still water cuts deeper than rapids. He will. Or he won't. Both are lessons.",
                        "I already have. You simply were not listening. ...That is the first lesson."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What about the hooded figure in the corner?",
                        "Thank you, Master Wu."
                    },
                    npcResponses = new string[]
                    {
                        "That one carries a weight that is not theirs. I have seen it before — people who stand at the edge of something vast and mistake the vertigo for purpose. Approach gently, if you approach at all.",
                        "Gratitude is a door. What matters is what you walk through it toward."
                    }
                }
            }
        );
    }

    static void CreateSharaDialogue()
    {
        CreateDialogue("Shara_Dialogue",
            "Hey! HEY! You're new here! Come sit! Master Wu won't mind — well, he won't say he minds, which is basically the same thing!",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "You seem... excited.",
                        "How long have you studied with Master Wu?",
                        "Is that your friend over there?"
                    },
                    npcResponses = new string[]
                    {
                        "Life is SHORT! And this tavern has the BEST stew! And I just landed a flying crescent kick this morning for the FIRST TIME! So yes! I am excited! Always!",
                        "Three years! Best three years of my life. Before that I was apprenticed to a blacksmith. Turns out I'm better at breaking things than making them. Master Wu says that's a kind of talent too.",
                        "Varien? He's the best! Super talented, way more than he thinks. He can do this thing where he catches a blade between two fingers — I STILL can't do that. He just needs to stop being so quiet about it. I keep telling him: confidence is a muscle! FLEX IT!"
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What's Master Wu really like?",
                        "You have a lot of energy for a tavern."
                    },
                    npcResponses = new string[]
                    {
                        "He's... honestly? He's the first person who looked at all THIS *gestures at herself* and didn't say 'too much.' He just said 'good, now aim it.' Changed my life.",
                        "You should see me in the training yard! Varien says I'm 'exhausting.' But he always says it while smiling, so I think it's a compliment. Right, Varien? ...He's pretending not to hear me. Classic Varien."
                    }
                }
            }
        );
    }

    static void CreateVarienDialogue()
    {
        CreateDialogue("Varien_Dialogue",
            "Oh — hello. Sorry, I was just... watching. I mean, not watching you. Watching the room. It's a habit. Master Wu says awareness is— sorry, you didn't ask for a lesson.",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "No, go on — what does Master Wu say?",
                        "You study with the old man too?",
                        "Your friend Shara speaks highly of you."
                    },
                    npcResponses = new string[]
                    {
                        "He says... awareness is the weapon you always carry. I'm still figuring out what he means half the time. Shara just nods and charges ahead. Maybe that works too. I don't know.",
                        "Five years now. Longer than Shara, though you wouldn't know it watching us. She picks things up so fast. I have to drill something a hundred times before it sticks. Master Wu says that's not weakness. I'm... working on believing that.",
                        "She does? She... really said that? That's — I mean, Shara's incredible. She can do things I've trained twice as long for. If she thinks I'm any good, then maybe... hm. Sorry, I don't usually talk this much."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What's your best technique?",
                        "You seem like you're too hard on yourself."
                    },
                    npcResponses = new string[]
                    {
                        "There's a deflection form — you redirect the opponent's force back through their own strike. It's subtle, not flashy like what Shara does. Nobody cheers when you win by not getting hit. But Master Wu said once that it was 'almost perfect.' Almost. I replay that in my head a lot.",
                        "Probably. Master Wu says the same thing. Shara says it louder. Maybe if enough people say it, I'll start listening. ...Thank you, though. That's kind."
                    }
                }
            }
        );
    }

    static void CreateSableDialogue()
    {
        CreateDialogue("Sable_Dialogue",
            "*muttering to themselves* ...the convergence is near, I can feel it in the walls... *notices you* ...You. You're standing too close.",
            new DialogueExchange[]
            {
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "Sorry, I didn't mean to interrupt.",
                        "Who are you talking to?",
                        "The barkeeper says you give her the creeps."
                    },
                    npcResponses = new string[]
                    {
                        "You didn't interrupt. You arrived. There's a difference. ...Sit if you want. Or don't. Free will is the one thing I still trust.",
                        "Myself. The walls. The space between the candle flames. Does it matter? At least they answer me honestly. People rarely do.",
                        "Wise woman. Fear is a gift — it tells you where the edges are. I am an edge. Best to know that upfront."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "What's this 'convergence' you mentioned?",
                        "The old master at the table seems to know something about you."
                    },
                    npcResponses = new string[]
                    {
                        "This tavern sits on a crossing — old paths, older than the stones. Something is threading through. I've been tracking it for months. When it arrives... *trails off, muttering* ...the alignment, yes, the third marker...",
                        "*pauses* ...Wu sees more than he lets on. That's what makes him dangerous. Not his hands — his eyes. He looked at me the first night I came here and said nothing. Nothing. That's how I knew he understood."
                    }
                },
                new DialogueExchange
                {
                    playerOptions = new string[]
                    {
                        "Are you okay? You seem... burdened.",
                        "I'll leave you to it."
                    },
                    npcResponses = new string[]
                    {
                        "...That's the first kind thing anyone has said to me in this tavern. *long pause* I am carrying something. Not for myself. For someone who can't carry it anymore. Don't ask me more. Not yet.",
                        "They all leave. But some come back. *returns to muttering* ...the third marker, yes, and when the light shifts..."
                    }
                }
            }
        );
    }
}
#endif