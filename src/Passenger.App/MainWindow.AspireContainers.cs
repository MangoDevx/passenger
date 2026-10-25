using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App;

public partial class MainWindow
{
    private bool leftoversScheduled;
    private bool leftoversLooked;

    /// <summary>A window keeping its state apart from the process's removes what a crash left for that state as it
    /// first shows, labelled or recorded (<see cref="ContainerLabels.LookForLeftovers"/>), as the process's start does
    /// for its own: held, so what it removes has gone once the window is quiet.</summary>
    private void LookForLeftovers(ProjectSession current)
    {
        if (current.Folder is not null)
        {
            Look();
        }
        else if (!leftoversScheduled)
        {
            leftoversScheduled = true;
            Opened += (_, _) => Look();
        }

        // The state folder as the window was given it, which its constructor, making its first session, has yet to take.
        void Look()
        {
            if (!leftoversLooked && !PathNames.Same(StateFolder, State.StateStore.DefaultRoot))
            {
                leftoversLooked = true;
                BackgroundWork.Track(ContainerLabels.LookForLeftovers(Engine, StateFolder));
            }
        }
    }

    private async Task ShowAspireContainerAsync(ProjectSession current, string id)
    {
        ShowToolWindow(ToolWindowIds.Containers);
        await ContainersWindow.RefreshAsync();
        if (session != current || current.IsEnded)
        {
            return;
        }
        if (ContainersWindow.Choose(row => row.Container is { } container && ContainerRow.SameId(id, container.Id)))
        {
            ContainersWindow.ShowTab("Log");
        }
        else
        {
            Say("The Aspire container is no longer available in the current engine");
        }
    }
}
