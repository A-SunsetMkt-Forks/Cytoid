using System.Collections.Generic;
using System.Linq;
using DG.Tweening;

using Proyecto26;
using UnityEngine;
using UnityEngine.UI;

public class CollectionDetailsScreen : Screen, LevelCardEventHandler, LevelBatchSelection
{
    public TransitionElement icons;
    
    public LoopVerticalScrollRect scrollRect;
    public RectTransform scrollRectPaddingReference;

    public Image coverImage;
    public Text titleText;
    public Text sloganText;
    
    public MediumAvatarWithName curatorAvatar;
    
    public TransitionElement batchActionBar;
    public Text batchActionBarMessage;
    public InteractableMonoBehavior batchActionCancelButton;
    public InteractableMonoBehavior batchActionSelectAllButton;
    public InteractableMonoBehavior batchActionDownloadButton;
    
    private readonly LevelBatchSelectionDownloadHandler levelBatchSelectionHandler = new LevelBatchSelectionDownloadHandler();

    public bool IsBatchSelectingLevels => levelBatchSelectionHandler.IsBatchSelectingLevels;
    public Dictionary<string, Level> BatchSelectedLevels => levelBatchSelectionHandler.BatchSelectedLevels;
    public LevelBatchAction LevelBatchAction => levelBatchSelectionHandler.LevelBatchAction;
    public bool OnLevelCardPressed(LevelView view) => levelBatchSelectionHandler.OnLevelCardPressed(view);
    public void OnLevelCardLongPressed(LevelView view) => levelBatchSelectionHandler.OnLevelCardLongPressed(view);

    public override void OnScreenChangeStarted(Screen from, Screen to)
    {
        base.OnScreenChangeStarted(from, to);
        if (from == this)
        {
            levelBatchSelectionHandler.LeaveBatchSelection();
        }
    }
    
    public override void OnScreenInitialized()
    {
        base.OnScreenInitialized();
        coverImage.sprite = null;
        titleText.text = "";
        sloganText.text = "";
        
        levelBatchSelectionHandler.OnEnterBatchSelection.AddListener(() =>
        {
            batchActionBar.transform.RebuildLayout();
            batchActionBar.Enter();
        });
        levelBatchSelectionHandler.OnLeaveBatchSelection.AddListener(() =>
        {
            batchActionBar.Leave();
        });
        levelBatchSelectionHandler.batchActionBarMessage = batchActionBarMessage;
        batchActionCancelButton.onPointerClick.AddListener(_ => levelBatchSelectionHandler.LeaveBatchSelection());
        batchActionSelectAllButton.onPointerClick.AddListener(_ =>
        {
            if (BatchSelectedLevels.Count < LoadedPayload.Collection.levels.Count)
            {
                BatchSelectedLevels.Clear();
                LoadedPayload.Collection.levels.ForEach(it =>
                {
                    if (!it.HasLocal(LevelType.User) && !it.HasLocal(LevelType.BuiltIn))
                    {
                        BatchSelectedLevels[it.Uid] = it.ToLevel(LevelType.User);
                    }
                });
                levelBatchSelectionHandler.UpdateBatchSelectionText();
            }
            else
            {
                BatchSelectedLevels.Clear();
            }
        });
        batchActionDownloadButton.onPointerClick.AddListener(_ => levelBatchSelectionHandler.DownloadBatchSelection());
    }

    public override void OnScreenEnterCompleted()
    {
        base.OnScreenEnterCompleted();
        var canvasRectTransform = Canvas.GetComponent<RectTransform>();
        var canvasScreenRect = canvasRectTransform.GetScreenSpaceRect();

        scrollRect.contentLayoutGroup.padding.top = (int) ((canvasScreenRect.height -
                                                            scrollRectPaddingReference.GetScreenSpaceRect().min.y) *
                canvasRectTransform.rect.height / canvasScreenRect.height) +
            48 - 156;
        scrollRect.transform.RebuildLayout();
    }

    public override void OnScreenBecameInactive()
    {
        base.OnScreenBecameInactive();
        if (LoadedPayload != null) LoadedPayload.ScrollPosition = scrollRect.verticalNormalizedPosition;
    }

    public override void OnScreenDestroyed()
    {
        base.OnScreenDestroyed();

        Destroy(scrollRect);
    }

    protected override void Render()
    {
        var collection = LoadedPayload.Collection;
        titleText.text = LoadedPayload.TitleOverride ?? collection.title;
        sloganText.text = LoadedPayload.SloganOverride ?? collection.slogan;
        sloganText.transform.parent.RebuildLayout();
        scrollRect.totalCount = collection.levels.Count;
        scrollRect.objectsToFill = collection.levels.Select(it => new LevelView{ Level = it.ToLevel(LoadedPayload.Type), DisplayOwner = true}).ToArray().Cast<object>().ToArray();
        scrollRect.RefillCells();
        if (LoadedPayload.ScrollPosition > -1)
        {
            scrollRect.SetVerticalNormalizedPositionFix(LoadedPayload.ScrollPosition);
        }

        base.Render();
    }
    
    protected override void LoadPayload(ScreenLoadPromise promise)
    {
        coverImage.sprite = null;
        coverImage.color = Color.black;

        if (IntentPayload.Collection != null)
        {
            promise.Resolve(IntentPayload);
            return;
        }
        
        SpinnerOverlay.Show();
        RestClient.Get<CollectionMeta>(new RequestHelper
            {
                Uri = $"{Context.ApiUrl}/collections/{IntentPayload.CollectionId}",
                Headers = Context.OnlinePlayer.GetRequestHeaders(),
                EnableDebug = true
            })
            .Then(meta =>
            {
                IntentPayload.Collection = meta;

                promise.Resolve(IntentPayload);
            })
            .CatchRequestError(error =>
            {
                Debug.LogError(error);
                Dialog.PromptGoBack("DIALOG_COULD_NOT_CONNECT_TO_SERVER".Get());

                promise.Reject();
            })
            .Finally(() => SpinnerOverlay.Hide());
    }

    protected override void OnRendered()
    {
        base.OnRendered();

        scrollRect.GetComponent<TransitionElement>()
            .Let(it =>
            {
                it.Leave(false, true);
                it.Enter();
            });

        icons.Leave(false, true);
        icons.Enter();

        curatorAvatar.SetModel(LoadedPayload.Collection.owner);
        
        if (coverImage.sprite == null || coverImage.sprite.texture == null)
        {
            AddTask(async token =>
            {
                Sprite sprite;
                try
                {
                    sprite = await Context.AssetMemory.LoadAsset<Sprite>(LoadedPayload.Collection.cover.CoverUrl,
                        AssetTag.CollectionCover, cancellationToken: token);
                }
                catch
                {
                    return;
                }

                if (sprite != null)
                {
                    coverImage.sprite = sprite;
                    coverImage.FitSpriteAspectRatio();
                    coverImage.DOColor(new Color(0.2f, 0.2f, 0.2f, 1), 0.4f);
                }
            });
        }
        else
        {
            coverImage.DOColor(new Color(0.2f, 0.2f, 0.2f, 1), 0.4f);
        }
    }

    public class Payload : ScreenPayload
    {
        public string CollectionId;
        public CollectionMeta Collection;
        public string TitleOverride;
        public string SloganOverride;
        public LevelType Type = LevelType.User;

        public float ScrollPosition = -1;
    }
    
    public new Payload IntentPayload => (Payload) base.IntentPayload;
    public new Payload LoadedPayload
    {
        get => (Payload) base.LoadedPayload;
        set => base.LoadedPayload = value;
    }
    
    public const string Id = "CollectionDetails";
    public override string GetId() => Id;
    
}
