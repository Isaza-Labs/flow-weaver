using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Compiler;

public interface IWorkflowYamlCompiler
{
    string Compile(WorkflowModel workflow);
}
